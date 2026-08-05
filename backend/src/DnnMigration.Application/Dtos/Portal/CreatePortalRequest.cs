namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Inbound request contract for <c>POST /api/v1/portals</c>.
/// </summary>
/// <remarks>
/// <para>
/// This type replaces the fifteen positional arguments of the legacy
/// <c>PortalController.CreatePortal</c> overload at
/// <c>Library/Components/Portal/PortalController.vb:L980</c>. Twelve of those arguments were supplied
/// by a control on the legacy signup screen and appear below as named properties; three were computed
/// by the code-behind and are deliberately absent, each recorded against the property group it would
/// have joined.
/// </para>
/// <para>
/// A create request describes only what is to be created: it carries no status, identifier, success
/// flag or error member, because the portal service reports its outcome through its return value and
/// the controller translates that into an HTTP status and body.
/// </para>
/// <para>
/// Legacy source of truth: the fifteen-argument signature and its body from
/// <c>Library/Components/Portal/PortalController.vb</c>; the call site and the origin of every argument
/// from <c>Website/admin/Portal/Signup.ascx.vb:L274</c>; and the field set, maximum lengths, validators
/// and message wording from <c>Website/admin/Portal/signup.ascx</c> and its resource file. This type
/// declares no rule and enforces none: every measured rule is documented on the property it governs so
/// that <c>Application/Validation/CreatePortalRequestValidator.cs</c> reproduces the legacy rule set
/// exactly - seven of the eight required-field validators on that screen guard a property below, and
/// the eighth guards a browser-only confirmation control that was never submitted.
/// </para>
/// <para>
/// Empty strings. The legacy null contract represents an absent string as the empty string rather than
/// as a database null, so an empty string may arrive on any string property below where a modern
/// reader would expect <see langword="null"/>, and the two are indistinguishable in data written by
/// the legacy application. No property converts between them: the boundary, not the domain model,
/// carries the legacy sentinel semantics.
/// </para>
/// </remarks>
public sealed class CreatePortalRequest
{
    // MIGRATION: the legacy CreatePortal returned an Integer and signalled failure with a NEGATIVE
    // sentinel identifier (PortalController.vb:L969, tested at L990 and at Signup.ascx.vb:L276, L280).
    // That same value is both the legacy absent-integer sentinel AND the IDENTITY seed of the Portals
    // primary key - a real, addressable PortalID, with the shipped default portal at the very next
    // value, zero - so a legacy caller could not tell a failed creation from a successfully located
    // portal. The replacement is a Result<PortalDetailDto> carrying success, value and failure reason as
    // distinct data. Nothing on this request encodes an outcome, and no property treats any particular
    // numeric or string value as meaning "absent".

    /// <summary>
    /// Gets or sets the display name of the new portal.
    /// </summary>
    /// <remarks>
    /// Legacy argument 1, <c>PortalName</c>, supplied by the <c>txtSiteName</c> text box
    /// (<c>maxlength</c> 128) and persisted to <c>Portals.PortalName</c>.
    /// Measured validation rules: required, message "Site Name Is Required."; maximum length 128; no
    /// format pattern.
    /// </remarks>
    public string? PortalName { get; set; }

    // MIGRATION: two documented behavioural differences attach to the alias, and neither is implemented
    // on this request.
    //
    //   CASING IS NOT PRESERVED, which is legacy behaviour retained rather than introduced.
    //   PortalAliasController.vb lower-cases the alias on every write (L31, L97) and keys its lookups by
    //   the lower-cased value (L52, L76); the signup screen lower-cased the field even earlier
    //   (Signup.ascx.vb:L183) and stripped a leading scheme (L184). The permitted character set is
    //   itself lower-case only, which makes the conversion lossless in practice. Normalisation is
    //   service behaviour; this property records only that a caller cannot rely on its own casing.
    //
    //   ALIAS RESOLUTION CHANGES FROM SUBSTRING TO EXACT MATCH, and this one IS a deliberate divergence.
    //   The legacy tenant-resolution procedure selected min(PortalID) with "where PortalAlias like '%' +
    //   @PortalAlias + '%'" (01.00.00.SqlDataProvider:L4569-L4600), so an alias that was a substring of
    //   another portal's resolved to the wrong tenant - a multi-tenant isolation defect. The resolution
    //   middleware matches exactly instead, and nothing here implies pattern, partial or wildcard
    //   matching.

    /// <summary>
    /// Gets or sets the HTTP alias through which the new portal is addressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 12, <c>PortalAlias</c>, consumed at <c>PortalController.vb:L1134</c> to create
    /// the portal's first alias row. Supplied by a text box of <c>maxlength</c> 128 that the screen
    /// labels "Portal Alias:". The backing column is <c>PortalAlias nvarchar(200) NOT NULL</c>.
    /// </para>
    /// <para>
    /// Callers must expect the stored alias to differ in case from the value they submit, and must not
    /// read partial, substring or pattern matching into it. Both differences are explained in the note
    /// above this property.
    /// </para>
    /// <para>
    /// Measured validation rules: required, message "Portal Name Is Required."; maximum length 128;
    /// and, for a child portal, restricted to <c>abcdefghijklmnopqrstuvwxyz0123456789-</c>, widened by
    /// <c>./:</c> for a parent portal (<c>Signup.ascx.vb:L192</c> and L207-L216), with the
    /// character-set message "The Portal Name Must Not Contain Spaces Or Punctuation." and the
    /// uniqueness message "The Portal Alias Name You Specified Already Exists. Please Choose A
    /// Different Portal Alias."
    /// </para>
    /// </remarks>
    public string? PortalAlias { get; set; }

    /// <summary>
    /// Gets or sets the portal description used for site metadata.
    /// </summary>
    /// <remarks>
    /// Legacy argument 7, a genuine <c>Portals</c> column assigned at
    /// <c>PortalController.vb:L1112</c>. Measured validation rules: maximum length 500, no validator
    /// declared on the legacy screen.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the portal keywords used for site metadata.
    /// </summary>
    /// <remarks>
    /// Legacy argument 8, persisted alongside <see cref="Description"/> at
    /// <c>PortalController.vb:L1113</c>. The interior capital <c>W</c> reproduces the legacy argument
    /// and column spelling exactly and is deliberate, even though the screen's label resource spells
    /// it "Keywords:": data-model fidelity governs the property name, label wording governs only the
    /// user interface. Measured validation rules: maximum length 500, no validator declared.
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
    /// An omitted value is MEANINGFUL, not merely missing. The legacy screen pre-filled the box with the
    /// literal placeholder <c>Portals/[PortalID]</c> and sent the empty string when the user left it
    /// untouched (<c>Signup.ascx.vb:L245-L249</c>); the service then substituted
    /// <c>"Portals/" + PortalID</c> (<c>PortalController.vb:L991-L992</c>), which cannot be evaluated
    /// before the portal has an identifier. This is one place where the empty-string sentinel is
    /// behaviourally load-bearing, so the service must treat <see langword="null"/>, the empty string
    /// and the literal placeholder text alike as a request for the server-side default; this property
    /// performs no defaulting.
    /// </para>
    /// <para>
    /// Only the portal-RELATIVE directory belongs here: the legacy code mapped it onto a physical path
    /// through the excluded Globals module, whose replacement is the hosting environment's content-root
    /// path, and no mapped or absolute path is accepted from a caller. Measured validation rules:
    /// maximum length 100, no validator declared; the screen reported an unusable folder after the fact
    /// with "The Home Folder you specified is not valid."
    /// </para>
    /// </remarks>
    public string? HomeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the name of the portal template that seeds the new portal's pages, modules and
    /// roles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 10. The code-behind appended the <c>.template</c> extension server-side before
    /// calling the controller (<c>Signup.ascx.vb:L270</c>), so a caller submits the template's name
    /// and the service owns the extension. The service reads the file when parsing the template
    /// (<c>PortalController.vb:L1064</c> and L1075) and additionally always parses a fixed
    /// administration template at L1082, which is not a caller input.
    /// </para>
    /// <para>
    /// A file name only, never a path: the directory was legacy argument 9 and is deliberately absent,
    /// as the note above <see cref="IsChildPortal"/> records.
    /// </para>
    /// <para>
    /// Measured validation rules: required, message "Please select a template file", declared with an
    /// initial value so the drop-down's unselected state failed validation. No maximum length and no
    /// format pattern.
    /// </para>
    /// </remarks>
    public string? TemplateFile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the new portal is a child portal addressed beneath an
    /// existing portal's alias rather than a parent portal with an alias of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 15 and the only non-string argument in the signature, derived by the
    /// code-behind from a Parent/Child radio-button list (<c>Signup.ascx.vb:L199</c>). The legacy
    /// screen additionally forced the flag true when the request did not originate from a host-level
    /// page (L187-L188), a decision that depended on ambient per-request state and now belongs to the
    /// service and its authorisation policy rather than to the caller.
    /// </para>
    /// <para>
    /// Non-nullable, matching the legacy <c>Boolean</c>. The legacy null contract cannot express an
    /// unset boolean - its boolean sentinel is <c>False</c> and its absence test consequently reports
    /// <c>False</c> itself as absent - so a deliberate "parent portal" and an omitted choice were
    /// already indistinguishable. Making this nullable would invent a third state the legacy data
    /// never recorded, so an absent value deserialises to <see langword="false"/>, the legacy default.
    /// </para>
    /// <para>
    /// Measured validation rules: none. The radio-button list always posted one of its two values.
    /// </para>
    /// </remarks>
    public bool IsChildPortal { get; set; }

    // MIGRATION: three of the fifteen legacy arguments are absent because the measured call site at
    // Website/admin/Portal/Signup.ascx.vb:L274 shows the code-behind COMPUTING them rather than reading
    // them from a control. Each is a server physical path, and each must be derived inside the portal
    // service from the hosting environment. Argument 9, TemplatePath, was the physical directory holding
    // the installation's templates, concatenated with TemplateFile to open a file on disk
    // (PortalController.vb:L1064, L1075) - it came from the excluded Globals module, and only the
    // template's NAME is accepted here. Argument 13, ServerPath, was the application root, excluded on
    // two independent grounds: it was never caller input, and accepting a server filesystem path over
    // HTTP is a path-traversal and information-disclosure hazard. Argument 14, ChildPath, was used
    // directly with Directory.Exists, Directory.CreateDirectory and File.Copy
    // (PortalController.vb:L1042-L1049) and is fully determined by ServerPath and PortalAlias, so a
    // caller supplying it could neither add information nor be trusted with it. No compensating property
    // is added: where a caller's intent was expressed at all, it survives as PortalAlias and
    // IsChildPortal.

    // MIGRATION: legacy arguments 2 to 6 were named FirstName, LastName, Username, Password and Email,
    // and each is renamed below with an Administrator prefix, because NONE of them is a Portals column.
    // The legacy parameter documentation describes all five as the "Portal Administrator's"
    // (PortalController.vb:L956-L960) and the body assigns them to a UserInfo instance and its Membership
    // and Profile members before passing it to UserController.CreateUser (L1000-L1013). The schema
    // agrees: a case-insensitive search of all eighty-eight upgrade scripts across the four object-name
    // forms finds no Email column on Portals in any CREATE TABLE or ALTER TABLE, and Email reaches a
    // portal-shaped result only through the terminal vw_Portals view, which left outer joins Users on the
    // portal's administrator identifier. The prefix states what the bare legacy names concealed: these
    // five values create the portal's first user. They are kept flat rather than nested so the wire
    // contract maps one-to-one onto the client model.

    /// <summary>
    /// Gets or sets the given name of the initial administrator user created together with the portal.
    /// </summary>
    /// <remarks>
    /// Legacy argument 2, assigned to the new user at <c>PortalController.vb:L1001</c> and to that
    /// user's profile at L1010. It also contributes to the display name, which the legacy code composes
    /// as the given and family names joined by a space (L1004); that composition is service behaviour
    /// and is not accepted here. Measured validation rules: required, message "First Name Is
    /// Required."; maximum length 100.
    /// </remarks>
    public string? AdministratorFirstName { get; set; }

    /// <summary>
    /// Gets or sets the family name of the initial administrator user created together with the portal.
    /// </summary>
    /// <remarks>
    /// Legacy argument 3, assigned to the new user at <c>PortalController.vb:L1002</c> and to that
    /// user's profile at L1011. Measured validation rules: required, message "Last Name Is Required.";
    /// maximum length 100.
    /// </remarks>
    public string? AdministratorLastName { get; set; }

    /// <summary>
    /// Gets or sets the sign-in name of the initial administrator user created together with the
    /// portal.
    /// </summary>
    /// <remarks>
    /// Legacy argument 4, assigned to the new user at <c>PortalController.vb:L1003</c>. The legacy
    /// screen looked the created user back up by this name afterwards, so it is the credential the new
    /// administrator signs in with. Measured validation rules: required, message "Username Is
    /// Required."; maximum length 100.
    /// </remarks>
    public string? AdministratorUsername { get; set; }

    // MIGRATION: the password store changed, and the change is deliberate rather than incidental. The
    // legacy membership provider stored passwords in a reversible format with retrieval enabled, and
    // the symmetric key that reversed them was itself committed to source control, so every historical
    // password was recoverable by anyone holding the repository; the baseline schema was worse still,
    // holding the password as a plain narrow string column directly on Users. The target hashes
    // passwords one way through Infrastructure/Security/BcryptPasswordHasher.cs, and password
    // RETRIEVAL is deliberately not carried forward to any endpoint, service or screen - reset
    // replaces it. Consequently this value can be supplied but can never be read back, and no response
    // DTO exposes a password member.
    //
    // The password POLICY, by contrast, is preserved verbatim rather than tightened: minimum length
    // seven, no non-alphanumeric character required, no question and answer, and email uniqueness not
    // enforced. Tightening it during a migration would deny existing users access to their own
    // accounts, so any hardening is a separate, explicit decision. Those rules belong to
    // Application/Validation/CreatePortalRequestValidator.cs and
    // Application/Options/PasswordPolicyOptions.cs, never here.

    /// <summary>
    /// Gets or sets the initial administrator's password. Write-only: accepted on create, never
    /// returned, never logged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 5, assigned to the new user's membership record at
    /// <c>PortalController.vb:L1005</c> and supplied by a password field of <c>maxlength</c> 20.
    /// </para>
    /// <para>
    /// Handling requirements. The value is hashed by
    /// <c>Infrastructure/Security/BcryptPasswordHasher.cs</c> before it is persisted and is retained in
    /// no other form. It must never appear in a log entry, trace, validation message or error response,
    /// so request logging must not serialise this request body verbatim. No response DTO carries a
    /// password.
    /// </para>
    /// <para>
    /// Measured validation rules: required, message "Password Is Required."; control maximum length 20;
    /// plus the preserved legacy policy recorded in the note above this property.
    /// </para>
    /// </remarks>
    public string? AdministratorPassword { get; set; }

    // MIGRATION: the legacy screen's password CONFIRMATION box is absent by design - it was never one
    // of the fifteen arguments and was never persisted, existing only to compare two browser inputs
    // ("The Password Values Entered Do Not Match."). The equivalent check belongs to a cross-field
    // validator on the client form, where both values are already present; transmitting a password twice
    // would widen its exposure without adding any safety.

    /// <summary>
    /// Gets or sets the electronic mail address of the initial administrator user created together
    /// with the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 6, assigned to the new user at <c>PortalController.vb:L1006</c> and supplied by
    /// a text box of <c>maxlength</c> 100. The backing column is <c>Users.Email</c>, whose TERMINAL
    /// shape is <c>nvarchar(256) NULL</c>: the baseline declared it <c>nvarchar(100) NOT NULL</c>, a
    /// later script dropped the column outright, and <c>03.00.13.SqlDataProvider:L109-L110</c> re-added
    /// it as <c>nvarchar(256) NULL</c>, which every terminal procedure parameter matches. The legacy
    /// screen also used the address as the destination of the portal-signup notification message.
    /// </para>
    /// <para>
    /// Measured validation rules: required, message "Email Is Required."; control maximum length 100;
    /// and, importantly, NO format pattern - the legacy screen declared no regular-expression
    /// validator on this field, and every validator on that screen is a required-field validator. The
    /// validator author must reproduce that rule set exactly and must not invent a format rule the
    /// legacy screen did not impose. The persisted width is 256, so a validator that caps this value
    /// must use the column width and not the control's.
    /// </para>
    /// <para>
    /// Uniqueness is not enforced. The membership provider ran with email uniqueness switched off, so
    /// two users of the same portal could share an address and existing data may already do so.
    /// </para>
    /// <para>
    /// The type is a plain nullable string rather than a domain value object, for three measured reasons:
    /// a wrapper exposing a <c>Value</c> member would oblige a client to post a nested object, breaking
    /// the client model and the integration-test assertions; its factory raises a domain exception on
    /// malformed input, whereas a request contract must not raise while being deserialised, because a
    /// malformed address has to surface as a validation failure; and the legacy pattern such a wrapper
    /// would encode (<c>Library/Components/Shared/Globals.vb:L132</c>) caps the top-level domain at four
    /// letters, so addresses under longer modern top-level domains fail it, as do some the legacy
    /// installer itself wrote. Validation is declared once, in the validator, where it can be relaxed for
    /// legacy data without changing this contract.
    /// </para>
    /// </remarks>
    public string? AdministratorEmail { get; set; }
}
