namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Inbound request contract for <c>POST /api/v1/portals</c>.
/// </summary>
/// <remarks>
/// <para>
/// This type replaces the fifteen positional arguments of the legacy
/// <c>PortalController.CreatePortal</c> overload declared at
/// <c>Library/Components/Portal/PortalController.vb:L980</c>:
/// <c>PortalName, FirstName, LastName, Username, Password, Email, Description,
/// KeyWords, TemplatePath, TemplateFile, HomeDirectory, PortalAlias, ServerPath,
/// ChildPath, IsChildPortal</c>. Twelve of those arguments were supplied by a
/// control on the legacy signup screen and appear below as named properties;
/// three were computed by the code-behind and are deliberately absent, each
/// recorded against the property group it would have joined.
/// </para>
/// <para>
/// The request is consumed by <c>PortalsController</c> and handed to
/// <c>PortalService.CreatePortalAsync</c>. That method reports its outcome
/// through its return value, so this request carries no status, identifier,
/// success flag or error member of any kind: a create request describes only
/// what is to be created.
/// </para>
/// <para>
/// Legacy source of truth. The fifteen-argument signature and its body were read
/// from <c>Library/Components/Portal/PortalController.vb</c>; the call site and
/// the origin of every argument from
/// <c>Website/admin/Portal/Signup.ascx.vb:L274</c>; the field set, maximum
/// lengths and validators from <c>Website/admin/Portal/signup.ascx</c>; and the
/// label and message wording from
/// <c>Website/admin/Portal/App_LocalResources/Signup.ascx.resx</c>.
/// </para>
/// <para>
/// Validation. This type declares no rule and enforces none. Every rule measured
/// on the legacy screen is documented on the property it governs so that
/// <c>Application/Validation/CreatePortalRequestValidator.cs</c> can reproduce
/// the legacy rule set exactly. Seven of the eight required-field validators on
/// the legacy screen guard a property of this request; the eighth guards a
/// browser-only confirmation control that was never submitted.
/// </para>
/// <para>
/// Empty strings. The legacy null contract in
/// <c>Library/Components/Shared/Null.vb</c> represents an absent string as the
/// empty string rather than as a database null: its string sentinel returns
/// <c>""</c> at L71-L75, and its absence test at L225-L226 reports <c>""</c> as
/// absent. An empty string may therefore arrive on any string property below
/// where a modern reader would expect <c>null</c>, and the two are
/// indistinguishable in data written by the legacy application. No property below
/// converts between them; the distinction is preserved exactly as received so
/// that the boundary, not the domain model, carries the legacy sentinel
/// semantics.
/// </para>
/// <para>
/// Response shape. The endpoint answers with
/// <c>Result&lt;PortalDetailDto&gt;</c>, produced by
/// <c>PortalService.CreatePortalAsync</c> and translated into an HTTP status code
/// and body by <c>PortalsController</c>. Success, the created portal and the
/// reason for a failure are all carried there, which is why no member of this
/// request describes an outcome.
/// </para>
/// </remarks>
public sealed class CreatePortalRequest
{
    // MIGRATION: the legacy CreatePortal returned an Integer and signalled failure
    // by returning a negative sentinel identifier -- documented at
    // PortalController.vb:L969 as "PortalId of the new portal if there are no
    // errors, ... otherwise.", tested at PortalController.vb:L990 and assigned
    // again by the code-behind's own catch block before being retested at
    // Signup.ascx.vb:L276 and L280. That same negative value is what the legacy
    // null contract returns as its absent-integer sentinel
    // (Library/Components/Shared/Null.vb:L41-L45) and what its absence test
    // reports as absent for an Integer (Null.vb:L210-L211) -- and it is
    // simultaneously the seed of the Portals primary key, whose IDENTITY seed is
    // that very negative number, declared at
    // Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77.
    // It is therefore a real, addressable PortalID rather than a marker of
    // absence, and the shipped "_default" portal occupies the very next value,
    // zero, inserted at 01.00.00.SqlDataProvider:L7125. A legacy caller could not
    // tell a failed creation from a successfully located portal. The replacement
    // is a Result<PortalDetailDto> returned by PortalService.CreatePortalAsync,
    // which carries success, value and failure reason as distinct data. Nothing
    // on this request encodes an outcome, and no property here treats any
    // particular numeric or string value as meaning "absent".

    /// <summary>
    /// Gets or sets the display name of the portal to be created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 1, <c>PortalName</c>, documented at
    /// <c>PortalController.vb:L955</c> as "Name of the portal to be created".
    /// Supplied by the <c>txtTitle</c> text box, which
    /// <c>Signup.ascx.vb:L274</c> passes as the first argument. The screen labels
    /// that box "Title:" through the <c>plTitle</c> label control
    /// (<c>signup.ascx:L51</c>), and the resource entry <c>plTitle.Help</c> reads
    /// "Give your site a title." The value lands on the <c>PortalName</c> column,
    /// declared <c>[nvarchar] (128) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L79</c>, and the control carries a matching
    /// <c>maxlength</c> of 128.
    /// </para>
    /// <para>
    /// Measured validation rules: maximum length 128. The legacy screen declared
    /// <b>no</b> validator on <c>txtTitle</c>. The similarly named
    /// <c>valPortalName</c> validator, whose message reads "Portal Name Is
    /// Required.", guards the <i>alias</i> box rather than this one -- see
    /// <see cref="PortalAlias"/>. The validator author must not infer a
    /// requiredness rule for this property from that message.
    /// </para>
    /// </remarks>
    public string? PortalName { get; set; }

    // MIGRATION: two documented behavioural differences attach to the alias, and
    // neither is implemented on this request.
    //
    //   Casing is not preserved, and that is legacy behaviour retained rather
    //   than introduced. PortalAliasController.vb lower-cases the alias on every
    //   write -- AddPortalAlias at L31 and UpdatePortalAliasInfo at L97 both pass
    //   HTTPAlias.ToLower -- and the read path keys its lookup by the lower-cased
    //   value at L52 and L76. The signup screen lower-cased the field even
    //   earlier, at Signup.ascx.vb:L183, and stripped a leading scheme at L184.
    //   The permitted character set for the field is itself lower-case only
    //   (Signup.ascx.vb:L192 and L207-L216), which is what makes the conversion
    //   lossless in practice. No casing logic is applied here; normalisation is
    //   service behaviour, and this property records only that a caller cannot
    //   rely on its own casing surviving.
    //
    //   Alias resolution changes from substring to exact match, and this one IS a
    //   deliberate divergence. The legacy tenant-resolution procedure
    //   GetPortalSettings selected min(PortalID) from Portals
    //   "where PortalAlias like '%' + @PortalAlias + '%'"
    //   (01.00.00.SqlDataProvider:L4569-L4600), so an alias that is a substring of
    //   another portal's alias resolved to the wrong tenant -- a multi-tenant
    //   isolation defect. Carrying it into new code would reproduce the defect, so
    //   Api/Middleware/PortalAliasResolutionMiddleware.cs resolves by exact match
    //   instead. Nothing here promises or implies pattern, partial or wildcard
    //   matching. The legacy code did have a wildcard convention for querying
    //   aliases across portals -- PortalAliasController.vb:L86-L88 calls
    //   GetPortalAliasByPortalID with a sentinel argument, backed by the predicate
    //   measured at 02.02.02.SqlDataProvider:L3869 -- but that belongs to a query
    //   surface, not to a create request, and no equivalent is encoded here.

    /// <summary>
    /// Gets or sets the HTTP alias through which the new portal is addressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 12, <c>PortalAlias</c>, documented at
    /// <c>PortalController.vb:L965</c> as "Portal Alias String" and consumed at
    /// <c>PortalController.vb:L1134</c>, which calls <c>AddPortalAlias</c> to
    /// create the portal's first alias row. Supplied by the
    /// <c>txtPortalName</c> text box (<c>maxlength</c> 128), which the screen
    /// labels "Portal Alias:" through the <c>plPortalAlias</c> label control
    /// (<c>signup.ascx:L39</c>); the resource entry <c>plPortalAlias.Help</c>
    /// reads "Enter the Alias for this Portal". The backing column is
    /// <c>PortalAlias [nvarchar] (200) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider:L78</c>).
    /// </para>
    /// <para>
    /// Casing is not preserved. The legacy write path lower-cases the alias
    /// before persisting it -- <c>PortalAliasController.AddPortalAlias</c> passes
    /// <c>HTTPAlias.ToLower</c> at
    /// <c>Library/Components/Portal/PortalAliasController.vb:L31</c> and
    /// <c>UpdatePortalAliasInfo</c> does the same at L97 -- and the read path
    /// keys its lookup by the lower-cased alias at L52 and L76. The legacy screen
    /// lower-cased the field even earlier, at <c>Signup.ascx.vb:L183</c>, and
    /// stripped a leading scheme at L184. Callers must therefore expect the
    /// stored alias to differ in case from the value they submit. This request
    /// applies no transformation of its own; normalisation is service behaviour.
    /// </para>
    /// <para>
    /// Resolution is exact-match. The legacy tenant-resolution procedure
    /// <c>GetPortalSettings</c> matched an alias with
    /// <c>where PortalAlias like '%' + @PortalAlias + '%'</c> and then took
    /// <c>min(PortalID)</c>
    /// (<c>01.00.00.SqlDataProvider:L4569-L4600</c>), so one portal's alias being
    /// a substring of another's resolved to the wrong tenant. The replacement,
    /// <c>Api/Middleware/PortalAliasResolutionMiddleware.cs</c>, resolves an
    /// alias by exact match. Nothing about this property implies partial,
    /// substring or pattern matching, and no wildcard value is recognised here.
    /// </para>
    /// <para>
    /// Measured validation rules: required, maximum length 128, and -- for a
    /// child portal -- restricted to the character set
    /// <c>abcdefghijklmnopqrstuvwxyz0123456789-</c>, widened by <c>./:</c> for a
    /// parent portal (<c>Signup.ascx.vb:L192</c> and L207-L216). The character
    /// set is lower-case only, which is what makes the lower-casing above
    /// lossless in practice. The screen's required-field validator is
    /// <c>valPortalName</c> (<c>signup.ascx:L40-L41</c>), message "Portal Name Is
    /// Required."; the character-set message is <c>InvalidName</c>, "The Portal
    /// Name Must Not Contain Spaces Or Punctuation."; and the uniqueness message
    /// is <c>DuplicatePortalAlias</c>, "The Portal Alias Name You Specified
    /// Already Exists. Please Choose A Different Portal Alias."
    /// (<c>Signup.ascx.vb:L260-L265</c>).
    /// </para>
    /// </remarks>
    public string? PortalAlias { get; set; }

    /// <summary>
    /// Gets or sets the portal description used for site metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 7, <c>Description</c>, documented at
    /// <c>PortalController.vb:L961</c> as "Description for the new portal".
    /// Supplied by the <c>txtDescription</c> text box (<c>signup.ascx:L56-L57</c>,
    /// multi-line, <c>maxlength</c> 500). It is a genuine <c>Portals</c> column:
    /// <c>PortalController.vb:L1112</c> assigns it to the freshly loaded portal
    /// and L1114-L1118 persists it.
    /// </para>
    /// <para>
    /// Measured validation rules: maximum length 500, no validator declared on
    /// the legacy screen.
    /// </para>
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the portal keywords used for site metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 8, <c>KeyWords</c>, documented at
    /// <c>PortalController.vb:L962</c> as "KeyWords for the new portal". Supplied
    /// by the <c>txtKeyWords</c> text box (<c>signup.ascx:L61-L62</c>,
    /// multi-line, <c>maxlength</c> 500) and persisted alongside
    /// <see cref="Description"/> at <c>PortalController.vb:L1113</c>.
    /// </para>
    /// <para>
    /// The interior capital <c>W</c> reproduces the legacy argument and column
    /// spelling exactly and is deliberate, even though the screen's own label
    /// resource spells it "Keywords:". Data-model fidelity governs the property
    /// name; the label wording governs only what the user interface displays.
    /// </para>
    /// <para>
    /// Measured validation rules: maximum length 500, no validator declared on
    /// the legacy screen.
    /// </para>
    /// </remarks>
    public string? KeyWords { get; set; }

    /// <summary>
    /// Gets or sets the portal-relative home directory for uploaded content, or
    /// leaves it unset so that the service derives the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 11, <c>HomeDirectory</c>. Supplied by the
    /// <c>txtHomeDirectory</c> text box (<c>signup.ascx:L47</c>,
    /// <c>maxlength</c> 100), whose backing column is
    /// <c>UploadDirectory [nvarchar] (100) NOT NULL</c> in the baseline schema
    /// (<c>01.00.00.SqlDataProvider:L80</c>), renamed later in the upgrade chain.
    /// </para>
    /// <para>
    /// An omitted value is meaningful, not merely missing. The legacy screen
    /// pre-filled the box with the literal placeholder
    /// <c>Portals/[PortalID]</c> and, when the user left it untouched, sent the
    /// empty string instead (<c>Signup.ascx.vb:L245-L249</c>). The service then
    /// substituted the default: <c>PortalController.vb:L991-L992</c> reads
    /// <c>If HomeDirectory = "" Then HomeDirectory = "Portals/" + intPortalId.ToString</c>,
    /// which cannot be evaluated before the portal has an identifier. This is one
    /// place where the empty-string sentinel described on the type is
    /// behaviourally load-bearing rather than incidental, so <c>null</c>, the
    /// empty string and the literal placeholder text must all be treated by
    /// <c>PortalService</c> as a request for the server-side default. That
    /// substitution stays in the service: this property performs no defaulting.
    /// </para>
    /// <para>
    /// Only the portal-relative directory belongs here. The legacy code mapped it
    /// onto a physical path through the excluded
    /// <c>DotNetNuke.Common.Globals.ApplicationPath</c>
    /// (<c>PortalController.vb:L994</c>), whose replacement is the hosting
    /// environment's own content-root path. No mapped or absolute path is
    /// accepted from a caller.
    /// </para>
    /// <para>
    /// Measured validation rules: maximum length 100, no validator declared on
    /// the legacy screen. The screen reported an unusable folder after the fact
    /// with the <c>InvalidHomeFolder</c> message, "The Home Folder you specified
    /// is not valid." (<c>Signup.ascx.vb:L251-L257</c>).
    /// </para>
    /// </remarks>
    public string? HomeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the name of the portal template that seeds the new portal's
    /// pages, modules and roles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 10, <c>TemplateFile</c>, documented at
    /// <c>PortalController.vb:L964</c> as "Template file". Supplied by the
    /// <c>cboTemplate</c> drop-down list (<c>signup.ascx:L66</c>); the
    /// code-behind appended the <c>.template</c> extension server-side before
    /// calling the controller (<c>Signup.ascx.vb:L270</c>), so a caller submits
    /// the template's name and the service owns the extension. The service reads
    /// the file when parsing the template
    /// (<c>PortalController.vb:L1064</c> and L1075) and additionally always
    /// parses the fixed <c>admin.template</c> at L1082, which is not a caller
    /// input.
    /// </para>
    /// <para>
    /// A file name only, never a path. The directory the template is read from
    /// was legacy argument 9 and is deliberately absent -- see the note above
    /// <see cref="IsChildPortal"/>.
    /// </para>
    /// <para>
    /// Measured validation rules: required. The screen's validator is
    /// <c>valTemplate</c> (<c>signup.ascx:L67-L68</c>), message "Please select a
    /// template file", declared with an initial value so that the drop-down's
    /// unselected state failed validation. No maximum length and no format
    /// pattern were declared.
    /// </para>
    /// </remarks>
    public string? TemplateFile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the new portal is a child portal
    /// addressed beneath an existing portal's alias rather than a parent portal
    /// with an alias of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 15, <c>IsChildPortal</c>, documented at
    /// <c>PortalController.vb:L968</c> as "True if this is a child portal" and
    /// the only non-string argument in the signature. Supplied by the
    /// <c>optType</c> radio-button list (<c>signup.ascx:L33-L36</c>), whose
    /// values are <c>P</c> for Parent and <c>C</c> for Child; the code-behind
    /// derived the flag as <c>blnChild = (optType.SelectedValue = "C")</c> at
    /// <c>Signup.ascx.vb:L199</c>. The legacy screen additionally forced the flag
    /// true when the request did not originate from a host-level page
    /// (<c>Signup.ascx.vb:L187-L188</c>), a decision that depended on ambient
    /// per-request state and now belongs to the service and its authorisation
    /// policy rather than to the caller.
    /// </para>
    /// <para>
    /// The type is a non-nullable <c>bool</c>, matching the legacy
    /// <c>Boolean</c>. The legacy null contract cannot express an unset boolean:
    /// its boolean sentinel returns <c>False</c>
    /// (<c>Library/Components/Shared/Null.vb:L76-L80</c>) and its absence test
    /// consequently reports <c>False</c> itself as absent at L227-L228, so a
    /// deliberate "parent portal" and an omitted choice were already
    /// indistinguishable in the legacy system. Making the property nullable here
    /// would invent a third state the legacy data never recorded, so the
    /// ambiguity is documented rather than papered over: an absent value in the
    /// request body deserialises to <c>false</c>, which is the legacy default of
    /// a parent portal.
    /// </para>
    /// <para>
    /// Measured validation rules: none. The radio-button list always posted one
    /// of its two values and carried no validator.
    /// </para>
    /// </remarks>
    public bool IsChildPortal { get; set; }

    // MIGRATION: three of the fifteen legacy arguments are absent because the
    // measured call site at Website/admin/Portal/Signup.ascx.vb:L274 shows the
    // code-behind computing them rather than reading them from a control. Each
    // is a server physical path, and each is now derived inside PortalService
    // from the hosting environment.
    //
    //   Argument 9, TemplatePath -- passed as Common.Globals.HostMapPath, the
    //   physical directory holding the installation's portal templates
    //   (Signup.ascx.vb:L274, corroborated at L170). PortalController.vb:L1064
    //   and L1075 concatenate it with TemplateFile to open a file on disk.
    //   DotNetNuke.Common.Globals is an excluded subsystem, and its path members
    //   are replaced by the hosting environment's content-root path. Only the
    //   template's name is accepted, on TemplateFile above.
    //
    //   Argument 13, ServerPath -- passed as GetAbsoluteServerPath(Request)
    //   (Signup.ascx.vb:L224) and documented at PortalController.vb:L966 as "The
    //   Path to the root of the Application". Excluded unconditionally, on two
    //   independent grounds: it was never caller input, and accepting a server
    //   filesystem path over HTTP is a path-traversal and
    //   information-disclosure hazard. It is derived server-side, never posted.
    //
    //   Argument 14, ChildPath -- passed as strServerPath & strPortalAlias
    //   (Signup.ascx.vb:L229) and documented at PortalController.vb:L967 as "The
    //   Path to the Child Portal Folder". PortalController.vb:L1042-L1049 uses it
    //   directly with Directory.Exists, Directory.CreateDirectory and a File.Copy
    //   whose source is the excluded Common.Globals.HostMapPath. It is a server
    //   physical path fully determined by ServerPath and PortalAlias, so a caller
    //   supplying it could neither add information nor be trusted with it.
    //
    // No compensating property is added for any of the three. Where a caller's
    // intent was expressed at all, it survives as PortalAlias and IsChildPortal.

    // MIGRATION: legacy arguments 2 to 6 were named FirstName, LastName,
    // Username, Password and Email, and each is renamed below with an
    // Administrator prefix. None of them is a Portals column. The legacy
    // parameter documentation itself says so -- PortalController.vb:L956-L960
    // describes all five as the "Portal Administrator's" -- and the body
    // confirms it: L1000-L1011 assign them to a UserInfo instance and its
    // Membership and Profile members, which L1013 then passes to
    // UserController.CreateUser. The schema agrees. Two independent
    // case-insensitive searches across all eighty-eight scripts in
    // Website/Providers/DataProviders/SqlDataProvider, covering the bare,
    // owner-qualified, bracketed and templated object-name forms, find no Email
    // column on Portals in any CREATE TABLE or ALTER TABLE statement; the only
    // real Portals table is created at 01.00.00.SqlDataProvider:L76. Email
    // reaches a portal-shaped result only through the terminal view, where
    // vw_Portals (04.05.00.SqlDataProvider:L1530) selects an unqualified Email at
    // L1574 that resolves through
    // "LEFT OUTER JOIN ...Users AS U ON P.AdministratorId = U.UserID" at L1587.
    // FirstName, LastName, Username and Password are likewise Users columns,
    // declared at 01.00.00.SqlDataProvider:L99, L100, and -- for the credential
    // pair -- L106 and L107. The prefix therefore states what the bare legacy
    // names concealed: these five values create the portal's first user, its
    // administrator. They are kept flat rather than nested in an administrator
    // sub-object so that the wire contract maps one-to-one onto the Angular
    // model in frontend/src/app/core/models.

    /// <summary>
    /// Gets or sets the given name of the initial administrator user created
    /// together with the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 2, <c>FirstName</c>, documented at
    /// <c>PortalController.vb:L956</c> as the "Portal Administrator's first
    /// name". Supplied by the <c>txtFirstName</c> text box
    /// (<c>signup.ascx:L79</c>, <c>maxlength</c> 100). It is assigned to the new
    /// user at <c>PortalController.vb:L1001</c> and to that user's profile at
    /// L1010, and it also contributes to the user's display name, which the
    /// legacy code composes as the given and family names joined by a space
    /// (L1004). That composition is service behaviour and is not accepted here.
    /// </para>
    /// <para>
    /// Measured validation rules: required, by the <c>valFirstName</c> validator
    /// (<c>signup.ascx:L79-L80</c>), message "First Name Is Required.";
    /// control maximum length 100.
    /// </para>
    /// </remarks>
    public string? AdministratorFirstName { get; set; }

    /// <summary>
    /// Gets or sets the family name of the initial administrator user created
    /// together with the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 3, <c>LastName</c>, documented at
    /// <c>PortalController.vb:L957</c> as the "Portal Administrator's last name".
    /// Supplied by the <c>txtLastName</c> text box (<c>signup.ascx:L84</c>,
    /// <c>maxlength</c> 100) and assigned to the new user at
    /// <c>PortalController.vb:L1002</c> and to that user's profile at L1011.
    /// </para>
    /// <para>
    /// Measured validation rules: required, by the <c>valLastName</c> validator
    /// (<c>signup.ascx:L84-L85</c>), message "Last Name Is Required.";
    /// control maximum length 100.
    /// </para>
    /// </remarks>
    public string? AdministratorLastName { get; set; }

    /// <summary>
    /// Gets or sets the sign-in name of the initial administrator user created
    /// together with the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 4, <c>Username</c>, documented at
    /// <c>PortalController.vb:L958</c> as the "Portal Administrator's username".
    /// Supplied by the <c>txtUsername</c> text box (<c>signup.ascx:L89</c>,
    /// <c>maxlength</c> 100) and assigned to the new user at
    /// <c>PortalController.vb:L1003</c>. The legacy screen looked the created
    /// user back up by this name afterwards, at
    /// <c>Signup.ascx.vb:L283</c>, so it is the credential the newly created
    /// administrator signs in with.
    /// </para>
    /// <para>
    /// Measured validation rules: required, by the <c>valUsername</c> validator
    /// (<c>signup.ascx:L89-L90</c>), message "Username Is Required.";
    /// control maximum length 100.
    /// </para>
    /// </remarks>
    public string? AdministratorUsername { get; set; }

    // MIGRATION: the password store changed, and the change is deliberate rather
    // than incidental. The legacy membership provider was registered with
    // passwordFormat="Encrypted" and enablePasswordRetrieval="true"
    // (Website/release.config:L236-L247), and the 3DES key that reverses every
    // stored password is committed to source control in the same file at
    // L89-L93 -- so every historical password is recoverable by anyone holding
    // the repository. The baseline schema was worse still: Users.Password was a
    // plain [nvarchar] (20) column (01.00.00.SqlDataProvider:L106) and the
    // shipped Host and Administrator accounts were seeded with the literal
    // passwords visible at L7205 and L7207. The target hashes passwords one-way
    // through Infrastructure/Security/BcryptPasswordHasher.cs, and password
    // RETRIEVAL is deliberately not carried forward to any endpoint, service or
    // screen; reset replaces it. Consequently this value can be supplied but can
    // never be read back, and no response DTO exposes a password member.
    //
    // The password POLICY, by contrast, is preserved verbatim rather than
    // tightened: minRequiredPasswordLength="7",
    // minRequiredNonalphanumericCharacters="0", requiresQuestionAndAnswer="false"
    // and requiresUniqueEmail="false" (Website/release.config:L236-L247).
    // Tightening it during a migration would deny existing users access to their
    // own accounts, so any hardening is a separate, explicit decision, taken
    // knowingly and not as a side effect. Those rules are implemented in
    // Application/Validation/CreatePortalRequestValidator.cs against
    // Application/Options/PasswordPolicyOptions.cs, never here.

    /// <summary>
    /// Gets or sets the initial administrator's password. Write-only: accepted on
    /// create, never returned, never logged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 5, <c>Password</c>, documented at
    /// <c>PortalController.vb:L959</c> as the "Portal Administrator's password"
    /// and assigned to the new user's membership record at
    /// <c>PortalController.vb:L1005</c>. Supplied by the <c>txtPassword</c> text
    /// box (<c>signup.ascx:L94-L96</c>, <c>maxlength</c> 20, rendered as a
    /// password field).
    /// </para>
    /// <para>
    /// Handling requirements. The value is hashed by
    /// <c>Infrastructure/Security/BcryptPasswordHasher.cs</c> before it is
    /// persisted and is not retained in any other form. It must never appear in
    /// a log entry, a trace, a validation message or an error response: the
    /// structured-logging configuration excludes sensitive data, and request
    /// logging must not serialise this request body verbatim. No response DTO
    /// carries a password, so a client that needs to display confirmation must
    /// rely on the created administrator's other details.
    /// </para>
    /// <para>
    /// Measured validation rules: required, by the <c>valPassword</c> validator
    /// (<c>signup.ascx:L95-L96</c>), message "Password Is Required.";
    /// control maximum length 20; and the preserved legacy policy of minimum
    /// length 7 with no non-alphanumeric character required and no question and
    /// answer, recorded in the note above this property.
    /// </para>
    /// </remarks>
    public string? AdministratorPassword { get; set; }

    // MIGRATION: the legacy screen paired the password box with a txtConfirm box
    // and a valConfirm required-field validator (signup.ascx:L100-L102), and
    // compared the two at Signup.ascx.vb:L220-L222, reporting the InvalidPassword
    // message "The Password Values Entered Do Not Match." on mismatch. That
    // second box is absent from this request by design. It was never one of the
    // fifteen arguments -- the call site at L274 passes txtPassword.Text alone --
    // and it was never persisted; it existed only to compare two browser inputs
    // before postback. The equivalent check is a cross-field validator on the
    // Angular form in frontend/src/app/features/portal/portal-form, where both
    // values are already present. Transmitting a password twice would widen its
    // exposure without adding any safety.

    /// <summary>
    /// Gets or sets the electronic mail address of the initial administrator user
    /// created together with the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 6, <c>Email</c>, documented at
    /// <c>PortalController.vb:L960</c> as the "Portal Administrator's email" and
    /// assigned to the new user at <c>PortalController.vb:L1006</c>. Supplied by
    /// the <c>txtEmail</c> text box (<c>signup.ascx:L106</c>, <c>maxlength</c>
    /// 100). The backing column is <c>Users.Email [nvarchar] (100) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider:L107</c>). The legacy screen also used the
    /// address as the destination of the portal-signup notification message
    /// (<c>Signup.ascx.vb:L296</c>).
    /// </para>
    /// <para>
    /// Measured validation rules: required, by the <c>valEmail</c> validator
    /// (<c>signup.ascx:L106-L107</c>), message "Email Is Required."; control
    /// maximum length 100; and, importantly, <b>no format pattern</b> -- the
    /// legacy screen declared no regular-expression validator on this field, and
    /// every validator on that screen is a required-field validator. The
    /// validator author must reproduce that rule set exactly and must not invent
    /// a format rule the legacy screen did not impose.
    /// </para>
    /// <para>
    /// Uniqueness is not enforced. The membership provider was registered with
    /// <c>requiresUniqueEmail="false"</c>
    /// (<c>Website/release.config:L236-L247</c>), so two users of the same portal
    /// could share an address and existing data may already do so.
    /// </para>
    /// <para>
    /// The type is a plain nullable string rather than a domain value object,
    /// for three measured reasons. A value-object wrapper exposing a
    /// <c>Value</c> member and forbidding a custom converter would oblige a
    /// client to post a nested object, which would break the Angular model and
    /// the integration-test assertions. Its factory raises a domain exception on
    /// a malformed input, and a request contract must not raise an exception
    /// while being deserialised -- a malformed address has to surface as a
    /// validation failure. And the legacy pattern such a wrapper would encode,
    /// <c>Library/Components/Shared/Globals.vb:L132</c>, caps the top-level
    /// domain at four letters, so addresses under longer modern top-level
    /// domains fail it, as does the shipped Host account whose address is the
    /// bare word seeded at <c>01.00.00.SqlDataProvider:L7205</c>. Validation is
    /// declared once, in the validator, where it can be relaxed for legacy data
    /// without changing this contract.
    /// </para>
    /// </remarks>
    public string? AdministratorEmail { get; set; }
}
