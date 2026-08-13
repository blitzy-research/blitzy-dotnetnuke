// MIGRATION: this type has NO legacy counterpart, because the legacy application had no refresh-token
// concept at all. Website/release.config configured forms authentication with a self-contained encrypted
// ticket (Website/release.config:L217-L246), so there was no server-side record of a session, nothing to
// rotate and nothing to revoke - FormsAuthentication.SignOut simply discarded the caller's cookie. The
// target's refresh rotation is therefore new behaviour rather than a port, and these settings describe the
// SHAPE OF THE STORE that holds it, distinct from the token LIFETIMES, which stay on JwtOptions beside the
// service that stamps them.
namespace DnnMigration.Application.Options;

/// <summary>
/// Strongly typed configuration describing which refresh-token store a deployment runs and how large the
/// in-process store is allowed to grow, bound from the configuration section named by
/// <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is a deliberately minimal, dependency-free plain object carrying primitives and no attributes,
/// for the reason recorded on every options class in this folder: the Application project references the
/// Domain project and nothing else, so nothing declared here may reach the configuration, hosting or
/// dependency-injection abstractions that perform the binding. The Api layer owns the binding call and adds
/// the rules only a host can decide; <see cref="Validate"/> states the invariants this object can judge
/// alone.
/// </para>
/// <para>
/// <strong>WHY THIS TYPE EXISTS, AND WHAT IT DOES NOT PRETEND TO DO.</strong> The refresh-token store this
/// solution ships is process-local: refresh state lives in the API process, so it is neither shared between
/// replicas nor carried across a restart. That is a deliberate consequence of the plan's own constraints -
/// AAP rule T4 forbids adding a table to the existing DotNetNuke schema, AAP section 0.6 freezes the
/// dependency inventory with no distributed-cache client in it, and AAP section 0.9.3 freezes a two-service
/// container topology - and it is recorded in MIGRATION_NOTES.md rather than absorbed silently. What this
/// type adds is not a shared store; it is the ability for a deployment to STATE which store it is running
/// and to have the host refuse to start when the statement and reality disagree. Before it existed, a
/// deployment that believed it had cross-process refresh continuity had no way to be told otherwise, and a
/// replacement store registered by mistake was equally invisible. Both are now start-up failures.
/// </para>
/// <para>
/// <strong>The accepted providers are a closed set, and one of them is a DECLARATION rather than an
/// implementation.</strong> <see cref="InProcessProvider"/> - or its alias <see cref="InMemoryProvider"/> -
/// selects the process-local store this solution ships. <see cref="SqlServerProvider"/> selects the shared,
/// durable store it also ships, which holds one table in a catalogue of its own and is therefore
/// replica-safe and restart-surviving; it is opt-in and defaulted off, so this repository alone remains a
/// working deployment without it.
/// <see cref="ExternalProvider"/> asserts that the deployment has registered its own
/// <c>IRefreshTokenStore</c> after <c>AddInfrastructure</c> - the last registration of a service wins, so no
/// change to this repository's Infrastructure layer is required to substitute one - and the host verifies
/// that assertion while it starts. Naming any other value is refused: this build resolves a store from the
/// container, not from a name, so a third value could only be a typo that silently fell back to the
/// process-local store.
/// </para>
/// <para>
/// The two numeric settings replace constants that used to be compiled into the store. Their defaults are
/// exactly the values those constants held, so a deployment that configures nothing behaves precisely as it
/// did before this section existed.
/// </para>
/// </remarks>
public sealed class RefreshTokenStoreOptions
{
    /// <summary>
    /// Name of the configuration section this class binds from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Api layer resolves this class from the section this constant names, so it binds against a shared
    /// constant rather than a repeated literal. Because the configuration providers translate a double
    /// underscore into a section separator, the container and process-environment override for the provider
    /// below is <c>RefreshTokenStore__Provider</c>, and the equivalent settings-file path is
    /// <c>RefreshTokenStore:Provider</c>.
    /// </para>
    /// <para>
    /// Deliberately a section of its own rather than three more keys under <c>Jwt</c>. The refresh-token
    /// LIFETIMES - <c>Jwt:RefreshTokenExpirationDays</c> and
    /// <c>Jwt:RefreshTokenAbsoluteExpirationDays</c> - are token policy, stamped by the token service and
    /// read by the store, so they belong beside the signing settings. What this section carries is the
    /// identity and capacity of the STORE, which is a deployment-topology concern: it changes when a
    /// deployment scales out, not when its token policy changes. Keeping them apart also avoids a
    /// configuration change this finding does not require - moving an existing key would break every
    /// deployment already setting it.
    /// </para>
    /// </remarks>
    public const string SectionName = "RefreshTokenStore";

    /// <summary>
    /// Provider name selecting the process-local store this solution ships: <c>InProcess</c>.
    /// </summary>
    /// <remarks>
    /// The default, and the only value under which this repository alone constitutes a working deployment.
    /// It carries a documented operational cost - refresh state does not survive a restart and is not shared
    /// between replicas - which is why the value is named rather than implied: a deployment that runs this
    /// store does so on the record.
    /// </remarks>
    public const string InProcessProvider = "InProcess";

    /// <summary>
    /// Provider name declaring that the deployment supplies its own store: <c>External</c>.
    /// </summary>
    /// <remarks>
    /// Selecting it registers nothing. It asserts that an <c>IRefreshTokenStore</c> other than this
    /// solution's has been registered after <c>AddInfrastructure</c>, and the host refuses to start if that
    /// assertion is false - which is the whole value of the name, because the failure it prevents is a
    /// deployment scaling out in the belief that it already had a shared store.
    /// </remarks>
    public const string ExternalProvider = "External";

    /// <summary>
    /// Provider name selecting the shared, durable store this solution also ships: <c>SqlServer</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OPT-IN AND DEFAULTED OFF, and it is the only accepted value under which refresh state survives a
    /// restart and is observed identically by every replica. It selects <c>SqlServerRefreshTokenStore</c>,
    /// which holds one table of its own in a catalogue of its own. That table is PROVISIONED OUT OF BAND,
    /// from <c>docker/sql/refresh-token-store.sql</c>, and the store never creates it: the running API probes
    /// for the table and refuses to serve session operations when it is absent. See
    /// <see cref="RemovedCreateTableSetting"/> for what that replaced and why.
    /// </para>
    /// <para>
    /// ⚠ THE CATALOGUE MUST NOT BE THE DOTNETNUKE DATABASE, and <see cref="Validate"/> refuses the
    /// configuration when the two connection strings name the same server and catalogue. AAP rule T4 makes
    /// the existing schema immutable, so nothing of this migration's may be created in it; a catalogue of its
    /// own keeps the durable option inside that rule instead of trading one against the other.
    /// </para>
    /// </remarks>
    public const string SqlServerProvider = "SqlServer";

    /// <summary>
    /// Accepted alias for <see cref="InProcessProvider"/>: <c>InMemory</c>.
    /// </summary>
    /// <remarks>
    /// The same store under the name the container templates and the deployment documentation use for it.
    /// Both names are accepted so a deployment that already declares one is not broken by the other, and
    /// <see cref="UsesInProcessStore"/> answers <see langword="true"/> for either. New configuration should
    /// prefer <see cref="InProcessProvider"/>, which names what the store IS rather than where it happens to
    /// keep its state.
    /// </remarks>
    public const string InMemoryProvider = "InMemory";

    /// <summary>Schema the shared store's table is created in when none is configured: <c>dbo</c>.</summary>
    public const string DefaultSchema = "dbo";

    /// <summary>Name of the shared store's table when none is configured.</summary>
    public const string DefaultTableName = "DnnMigrationRefreshTokens";

    /// <summary>
    /// Smallest tracked-generation ceiling a deployment may configure: <c>1000</c>.
    /// </summary>
    /// <remarks>
    /// A floor rather than a preference, and it is derived rather than chosen. Each sign-in adds one tracked
    /// generation and each rotation adds one more, because a spent generation is retained as the signal that
    /// makes a replay recognisable; one caller refreshing every half hour for a thirty-day family lifetime
    /// therefore accounts for roughly 1,400 generations on its own. A ceiling below this floor would let a
    /// single active caller evict its own family, turning the capacity bound from a far-off safety limit into
    /// a routine cause of unexpected sign-outs. Enforced by the Api layer's start-up validator.
    /// </remarks>
    public const int MinimumTrackedTokenCeiling = 1_000;

    /// <summary>
    /// Largest tracked-generation ceiling a deployment may configure: <c>1000000</c>.
    /// </summary>
    /// <remarks>
    /// Roughly a hundred megabytes of tracked state at the store's measured cost of about a hundred bytes
    /// per generation, which is already a substantial share of what the API container is given. The bound
    /// exists to stop a deployment answering "we need more refresh capacity" by growing a process-local
    /// dictionary indefinitely: past this point the honest answer is a shared store registered behind
    /// <c>IRefreshTokenStore</c>, not a larger ceiling, because the memory it would consume is per replica
    /// and is lost on every restart. Enforced by the Api layer's start-up validator.
    /// </remarks>
    public const int MaximumTrackedTokenCeiling = 1_000_000;

    /// <summary>
    /// Longest same-client concurrent-use grace a deployment may configure, in seconds: <c>60</c>.
    /// </summary>
    /// <remarks>
    /// The grace is the window in which a second presentation of an already-spent refresh token by the SAME
    /// client fingerprint is treated as a near-simultaneous retry rather than as a theft. It exists because
    /// a client whose first exchange was interrupted mid-flight would otherwise have its whole family
    /// revoked. A long window weakens that detection, since it is also the window in which a stolen token
    /// replayed from a client presenting the same fingerprint would be forgiven, so the upper bound is
    /// deliberately far below any token lifetime. Enforced by the Api layer's start-up validator.
    /// </remarks>
    public const int MaximumConcurrentUseGraceSeconds = 60;

    /// <summary>
    /// Shortest retention a deployment may configure for a revoked refresh record, in hours: <c>1</c>.
    /// </summary>
    /// <remarks>
    /// PRIV-02. A revoked record keeps its token digest so that a later presentation of that family is
    /// recognisable as a replay rather than as an unknown value; erasing it the instant a sign-out completes
    /// would discard the one signal that makes credential theft visible after the fact. One hour is the floor
    /// because a replay following a stolen sign-out arrives in minutes, not days. Zero is deliberately NOT
    /// permitted: it would make the record's removal simultaneous with its revocation, which is the same as
    /// having no signal at all. Enforced by <see cref="Validate"/>.
    /// </remarks>
    public const int MinimumRevokedRetentionHours = 1;

    /// <summary>
    /// Longest retention a deployment may configure for a revoked refresh record, in hours: <c>720</c>
    /// (thirty days).
    /// </summary>
    /// <remarks>
    /// PRIV-02. The upper bound is a data-minimisation limit rather than a technical one. A revoked record is
    /// personal data about a subject who has already signed out, and its usefulness decays to nothing long
    /// before a month elapses; a deployment that wants a longer forensic trail should keep it in the audit
    /// sink, which is designed to be retained, rather than in a live credential table. Enforced by
    /// <see cref="Validate"/>.
    /// </remarks>
    public const int MaximumRevokedRetentionHours = 720;

    /// <summary>
    /// Shortest interval between reclamation sweeps a deployment may configure, in minutes: <c>1</c>.
    /// </summary>
    /// <remarks>
    /// PRIV-02. The sweep is a single set-based statement, so a short interval is cheap; the floor exists only
    /// to stop a mistyped zero turning the sweep into a busy loop against the store. Enforced by
    /// <see cref="Validate"/>.
    /// </remarks>
    public const int MinimumRetentionSweepMinutes = 1;

    /// <summary>
    /// Longest interval between reclamation sweeps a deployment may configure, in minutes: <c>1440</c> (one
    /// day).
    /// </summary>
    /// <remarks>
    /// PRIV-02. A sweep that runs less often than daily cannot honour an hour-granular retention setting, so
    /// the two bounds are related rather than independent: past this point the configured retention would be
    /// a statement about intent rather than about behaviour. Enforced by <see cref="Validate"/>.
    /// </remarks>
    public const int MaximumRetentionSweepMinutes = 1_440;

    /// <summary>
    /// Longest description of a configured provider that a validation failure will quote: <c>64</c>
    /// characters.
    /// </summary>
    /// <remarks>
    /// A validation failure quotes the value it refused, because "not one of the accepted names" without
    /// saying what arrived is a diagnostic an operator cannot act on. The quotation is bounded so that a
    /// pasted-in file or an accidentally enormous environment variable cannot put an unbounded string into a
    /// start-up log.
    /// </remarks>
    private const int MaximumQuotedProviderLength = 64;

    /// <summary>
    /// Gets or sets the name of the refresh-token store this deployment runs.
    /// </summary>
    /// <value>
    /// <see cref="InProcessProvider"/> or <see cref="ExternalProvider"/>, compared without regard to case
    /// or surrounding whitespace. Defaults to <see cref="InProcessProvider"/>.
    /// </value>
    /// <remarks>
    /// Comparison ignores case and trims, because a value that arrives from an environment variable is
    /// routinely typed by hand and <c>inprocess</c> is unambiguously the same intent as <c>InProcess</c>.
    /// Any other value is refused outright rather than treated as a synonym for the default - see the
    /// remarks on this class for why a silent fallback is the failure worth preventing.
    /// </remarks>
    public string Provider { get; set; } = InProcessProvider;

    /// <summary>
    /// Gets or sets how many refresh-token generations the in-process store tracks at once.
    /// </summary>
    /// <value>
    /// A ceiling between <see cref="MinimumTrackedTokenCeiling"/> and
    /// <see cref="MaximumTrackedTokenCeiling"/> inclusive. Defaults to <c>100000</c>, which is the value
    /// the store previously held as a compiled constant.
    /// </value>
    /// <remarks>
    /// <para>
    /// A bound is mandatory rather than defensive: nothing leaves the store before its family's absolute
    /// ceiling passes, so without one a caller holding valid credentials could grow the tracked set
    /// indefinitely. When the ceiling is reached, the families closest to their own ceiling are retired
    /// first, so the cost is that the OLDEST refresh families stop refreshing and their holders sign in
    /// again - a bounded availability cost, never a failure to serve.
    /// </para>
    /// <para>
    /// Ignored when <see cref="Provider"/> names an external store, because the ceiling describes the
    /// in-process dictionary and a replacement store's capacity is its own concern. It is still validated in
    /// that case, so a deployment moving back to the in-process store cannot discover a bad value later.
    /// </para>
    /// </remarks>
    public int MaximumTrackedTokens { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the same-client concurrent-use grace, in seconds.
    /// </summary>
    /// <value>
    /// Zero to <see cref="MaximumConcurrentUseGraceSeconds"/> inclusive, where zero disables the grace and
    /// makes any second presentation of a spent token a replay. Defaults to <c>5</c>, which is the value the
    /// store previously held as a compiled constant.
    /// </value>
    /// <remarks>
    /// Zero is permitted and is the strictest setting: it treats every reuse as theft and revokes the
    /// family. It is not the default because an interrupted exchange - a dropped response, a retried request
    /// - is a real occurrence that would otherwise sign the caller out, and the fingerprint comparison
    /// already confines the forgiveness to the client that spent the token.
    /// </remarks>
    public int ConcurrentUseGraceSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets how long a REVOKED refresh record is retained before it is erased, in hours.
    /// </summary>
    /// <value>
    /// <see cref="MinimumRevokedRetentionHours"/> to <see cref="MaximumRevokedRetentionHours"/> inclusive.
    /// Defaults to <c>24</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// PRIV-02. THIS IS THE DOCUMENTED MINIMUM PERIOD, AND IT REPLACES AN UNBOUNDED ONE. A revoked record used
    /// to live until its family's absolute ceiling elapsed - up to the whole refresh lifetime - because the only
    /// reclamation either store performed was expiry-based. So an ordinary sign-out left a row naming the
    /// account, the tenant and the token digest for days after the session it described had ended, and a
    /// deployment nobody signed in to again kept it for good.
    /// </para>
    /// <para>
    /// Twenty-four hours is the default because the record's only remaining purpose is the replay signal: a
    /// stolen credential presented after a sign-out is what a retained digest makes recognisable, and that
    /// attempt arrives within minutes or hours of the theft rather than days. Keeping it longer retains
    /// personal data for a signal nobody will read.
    /// </para>
    /// <para>
    /// It bounds REVOKED records only. A record that is still redeemable is retained until its family ceiling
    /// whatever this value says, because erasing a live session's record would sign its holder out.
    /// </para>
    /// </remarks>
    public int RevokedRecordRetentionHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets how often the operation-independent reclamation sweep runs, in minutes.
    /// </summary>
    /// <value>
    /// <see cref="MinimumRetentionSweepMinutes"/> to <see cref="MaximumRetentionSweepMinutes"/> inclusive.
    /// Defaults to <c>60</c>.
    /// </value>
    /// <remarks>
    /// PRIV-02. Reclamation used to happen only as a side effect of issuing or rotating a token, which means an
    /// installation with no sign-in traffic never reclaimed anything - and that is precisely the installation
    /// where nobody is watching. The sweep is driven by a hosted service on this interval instead, so the
    /// retention above is a statement about behaviour rather than about intent.
    /// </remarks>
    public int RetentionSweepMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets the connection string of the catalogue holding the shared store's table.
    /// </summary>
    /// <remarks>
    /// Required when, and read only when, <see cref="Provider"/> selects <see cref="SqlServerProvider"/>. It
    /// must name a catalogue other than the application's own: see the remarks on
    /// <see cref="SqlServerProvider"/> for why, and <see cref="Validate"/> for the check that enforces it.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>Gets or sets the schema holding the shared store's table.</summary>
    public string Schema { get; set; } = DefaultSchema;

    /// <summary>Gets or sets the name of the shared store's table.</summary>
    public string TableName { get; set; } = DefaultTableName;

    /// <summary>
    /// Name of the setting this class no longer carries, retained so the host can refuse a configuration
    /// that still sets it rather than binding it to nothing: <c>CreateTableIfMissing</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-05. The setting used to exist, defaulted to <see langword="true"/>, and let the store
    /// issue <c>CREATE TABLE</c> and <c>CREATE INDEX</c> under the API's own identity on first use. That is
    /// schema authorship at runtime by the process that serves requests, which AAP rule T4 forbids, and it
    /// meant the API's principal had to hold rights it needs for nothing else - the classic case for
    /// separating the identity that changes a schema from the one that reads and writes rows. The table is
    /// now provisioned OUT OF BAND from <c>docker/sql/refresh-token-store.sql</c> and the store only ever
    /// probes for it.
    /// </para>
    /// <para>
    /// The name survives as a constant because binding IGNORES unknown keys: a deployment that had set it
    /// would otherwise have its setting silently disregarded, and would discover the change only when the
    /// store refused to start against an unprovisioned catalogue. The Api layer's validator looks the key up
    /// and refuses to start with a message naming the script instead.
    /// </para>
    /// </remarks>
    public const string RemovedCreateTableSetting = "CreateTableIfMissing";

    /// <summary>Gets or sets the command timeout, in seconds, the shared store issues its statements with.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets a value indicating whether the deployment states, on the record, that it runs exactly ONE
    /// API instance and therefore accepts a refresh-token store that is neither shared between replicas nor
    /// carried across a restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-06. The process-local store is the shipped default, and in <c>Production</c> that default
    /// is only correct for a single-instance deployment - which the container topology this solution ships
    /// happens to be. What was missing is any way for the deployment to be WRONG about it and be told. Scaling
    /// the API to two replicas required no code change, no configuration change and produced no warning: a
    /// sign-out performed against replica A left the family exchangeable on replica B, a restart forgot every
    /// family it had issued, and both failures present as intermittent session behaviour that no log explains.
    /// The limitation was documented in prose, which a deployment does not read at scale-out time.
    /// </para>
    /// <para>
    /// <strong>SO IN PRODUCTION THE HOST NOW REFUSES TO START unless one of two things is true:</strong> the
    /// active store is authoritative across replicas - <see cref="SqlServerProvider"/>, or a deployment-supplied
    /// store that reports itself so - or this acknowledgement is set. Either is a deliberate, recorded decision;
    /// what is no longer possible is arriving at a replica-local store by default and not knowing.
    /// </para>
    /// <para>
    /// It is deliberately NOT a switch that changes behaviour. Setting it stores nothing differently, and it
    /// grants no capability: its whole function is to make the operator's own claim explicit, so that a later
    /// scale-out is a decision to revisit rather than a silent regression. Set it only where a single instance
    /// is genuinely enforced by the topology.
    /// </para>
    /// <para>
    /// Non-production environments are unaffected, because the failure it guards against is a production
    /// scale-out and requiring the ceremony of a developer machine would train operators to set it reflexively -
    /// which is exactly how an acknowledgement stops meaning anything.
    /// </para>
    /// </remarks>
    public bool AcknowledgeSingleInstance { get; set; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Provider"/> names the process-local store this solution
    /// ships.
    /// </summary>
    /// <remarks>
    /// Declared here, beside the value it interprets, because there are three consumers - the Api layer's
    /// start-up validator, the Infrastructure layer's start-up topology check, and the health probe that
    /// reports which store is active - and each restating the comparison in its own words is how three
    /// consumers come to disagree about one setting.
    /// </remarks>
    public bool UsesInProcessStore =>
        MatchesProvider(InProcessProvider) || MatchesProvider(InMemoryProvider);

    /// <summary>
    /// Gets a value indicating whether <see cref="Provider"/> declares a deployment-supplied store.
    /// </summary>
    public bool UsesExternalStore => MatchesProvider(ExternalProvider);

    /// <summary>
    /// Gets a value indicating whether the configured provider selects this solution's shared, durable store.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="UsesExternalStore"/>, which claims a store this repository did not write. A
    /// shared store IS authoritative across replicas and does survive a restart, which is why the container
    /// registers a different implementation for it and why the topology check treats the two names apart.
    /// </remarks>
    public bool UsesSharedStore => MatchesProvider(SqlServerProvider);

    /// <summary>Gets the shared store's table name, bracket-quoted and schema-qualified.</summary>
    public string QualifiedTableName =>
        FormattableString.Invariant($"[{Quote(Schema)}].[{Quote(TableName)}]");

    /// <summary>
    /// Reports every way in which the values bound onto this instance are unusable, so that a misconfigured
    /// deployment fails while the host is starting rather than midway through a request.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or an empty
    /// collection when the instance is usable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The collection shape is deliberate: options validation has to report EVERY failure at once, because
    /// an operator fixing one setting per restart is the outcome a single-failure result produces. It also
    /// keeps this class free of any dependency - the return type and the messages use base-class-library
    /// types only, so binding, hosting and validation packages all stay in the Api layer where the migration
    /// plan places them.
    /// </para>
    /// <para>
    /// The rules here are the ones this object can judge alone: that the provider is a name this build
    /// recognises, and that neither number is outside the range in which the store can function at all. The
    /// narrower operational bounds - the floor and ceiling on tracked generations and the ceiling on the
    /// grace - are POLICY, so the Api layer's validator adds them alongside this call. A value that both
    /// halves refuse is reported twice, in each half's own wording; that is deliberate, because the report
    /// is a start-up abort listing every problem found, and naming one setting twice is not misleading
    /// whereas dropping a rule to keep the list tidy is.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Validate(string? applicationConnectionString = null)
    {
        List<string> failures = [];

        if (!UsesInProcessStore && !UsesExternalStore && !UsesSharedStore)
        {
            failures.Add(
                $"{SectionName}:{nameof(Provider)} is {QuoteProvider()}, which this build does not "
                + $"recognise. Use '{InProcessProvider}' (or its alias '{InMemoryProvider}') for the "
                + $"process-local store this solution ships, '{SqlServerProvider}' for the shared durable "
                + $"store it also ships, or '{ExternalProvider}' to declare that the deployment registers its own "
                + "IRefreshTokenStore. An unrecognised name is refused rather than treated as the default, "
                + "because a typo that silently selected the process-local store would leave a deployment "
                + "believing it had a shared one.");
        }

        if (MaximumTrackedTokens < 1)
        {
            // Every number reaches the message through an invariant conversion first, so the concatenation
            // below interpolates strings only and cannot pick up a culture.
            string configured = FormattableString.Invariant($"{MaximumTrackedTokens}");

            failures.Add(
                $"{SectionName}:{nameof(MaximumTrackedTokens)} is {configured}, which leaves the store no "
                + "capacity at all: every generation would be evicted as it was issued, so no refresh token "
                + "could ever be redeemed and every caller would be signed out at the first refresh.");
        }

        if (ConcurrentUseGraceSeconds < 0)
        {
            string configured = FormattableString.Invariant($"{ConcurrentUseGraceSeconds}");

            failures.Add(
                $"{SectionName}:{nameof(ConcurrentUseGraceSeconds)} is {configured}, which is negative. A "
                + "negative grace cannot be expressed as a window; use 0 to disable the grace and treat "
                + "every reuse of a spent token as a replay.");
        }

        // PRIV-02. Both retention settings govern EVERY store, so they are validated above the shared-store
        // early return rather than below it: the process-local store reclaims on the same schedule and honours
        // the same revoked-record window.
        if (RevokedRecordRetentionHours is < MinimumRevokedRetentionHours or > MaximumRevokedRetentionHours)
        {
            string configured = FormattableString.Invariant($"{RevokedRecordRetentionHours}");
            string floor = FormattableString.Invariant($"{MinimumRevokedRetentionHours}");
            string ceiling = FormattableString.Invariant($"{MaximumRevokedRetentionHours}");

            failures.Add(
                $"{SectionName}:{nameof(RevokedRecordRetentionHours)} is {configured}, which is outside "
                + $"{floor} to {ceiling}. Below the floor a revoked record would be erased before a replay of "
                + "its family could be recognised, which discards the one signal that makes a stolen "
                + "credential visible after a sign-out; above the ceiling the record is personal data kept "
                + "for a signal nobody will read, and a longer forensic trail belongs in the audit sink.");
        }

        if (RetentionSweepMinutes is < MinimumRetentionSweepMinutes or > MaximumRetentionSweepMinutes)
        {
            string configured = FormattableString.Invariant($"{RetentionSweepMinutes}");
            string floor = FormattableString.Invariant($"{MinimumRetentionSweepMinutes}");
            string ceiling = FormattableString.Invariant($"{MaximumRetentionSweepMinutes}");

            failures.Add(
                $"{SectionName}:{nameof(RetentionSweepMinutes)} is {configured}, which is outside {floor} to "
                + $"{ceiling}. Zero or less would turn the reclamation sweep into a busy loop against the "
                + "store; above the ceiling the sweep runs less often than the retention it is meant to "
                + $"enforce, so {nameof(RevokedRecordRetentionHours)} would describe an intention rather than "
                + "a behaviour.");
        }

        if (!UsesSharedStore)
        {
            // Nothing below describes the process-local store, and a deployment running it must not be
            // failed for leaving a connection string, a schema or a table name unset.
            return failures;
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            failures.Add(FormattableString.Invariant(
                $"{SectionName}:{nameof(ConnectionString)} is required when the '{SqlServerProvider}' provider is selected."));
        }

        if (!IsPlainIdentifier(Schema))
        {
            failures.Add(FormattableString.Invariant(
                $"{SectionName}:{nameof(Schema)} must be a plain SQL identifier."));
        }

        if (!IsPlainIdentifier(TableName))
        {
            failures.Add(FormattableString.Invariant(
                $"{SectionName}:{nameof(TableName)} must be a plain SQL identifier."));
        }

        if (CommandTimeoutSeconds is < 1 or > 600)
        {
            failures.Add(FormattableString.Invariant(
                $"{SectionName}:{nameof(CommandTimeoutSeconds)} must be between 1 and 600."));
        }

        // ⚠ THE CHECKS THAT KEEP THE DURABLE OPTION INSIDE RULE T4, AND THEY ARE DELIBERATELY NOT A
        // STRING-EQUALITY TEST ANY MORE.
        //
        // MIGRATION: SEC-07. The previous rule refused the configuration only when the two connection strings
        // named the same server AND the same catalogue, compared as raw text. Both halves leaked. A host is
        // spellable as `localhost`, `127.0.0.1`, `(local)`, `.`, `tcp:localhost,1433`, the machine name or a
        // named instance, and every one of those pairs is the same server while comparing unequal - so the
        // guard was bypassed by writing the host differently on one side. Worse, a connection string that
        // omitted `Database` altogether read as "no catalogue" and returned false, which meant the ONE
        // configuration most likely to land in the DotNetNuke database - the default catalogue of a login
        // created for it - was the one the guard waved through.
        //
        // The fix inverts the question. Proving two connection strings address DIFFERENT databases means
        // canonicalising host spellings, instance names, ports, aliases and DNS - a problem with no end and no
        // way to be sure it is finished. Proving they name different CATALOGUES is a single ordinal comparison,
        // and it is sufficient: the session catalogue must be a catalogue provisioned for this purpose, and
        // such a catalogue does not share the DotNetNuke catalogue's NAME wherever it is hosted. So the rule
        // is: name the catalogue explicitly, do not name the application's, and do not name a system one. Each
        // is decidable from the text alone, which is why it holds where the previous rule did not.
        //
        // The cost is one refusal a permissive rule would have allowed: the same catalogue name on a genuinely
        // different server. That deployment is asked to give its session catalogue a distinct name, which is
        // what an operator would do anyway, and the direction of the error is the safe one.
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            string sessionCatalogue = ReadCatalogue(ConnectionString!);

            if (sessionCatalogue.Length == 0)
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{SectionName}:{nameof(ConnectionString)} must name the session catalogue explicitly, through a 'Database' or 'Initial Catalog' keyword.")
                    + " A connection string that names none resolves to whatever the login's default"
                    + " catalogue happens to be, which is routinely the DotNetNuke database itself - so"
                    + " the isolation this setting exists to guarantee could not be established at all.");
            }
            else if (IsSystemCatalogue(sessionCatalogue))
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{SectionName}:{nameof(ConnectionString)} names the system catalogue '{Redact(sessionCatalogue)}'.")
                    + " Session state is held in a catalogue provisioned for it, never in a server's own"
                    + " administrative databases.");
            }
            else if (!string.IsNullOrWhiteSpace(applicationConnectionString)
                && string.Equals(
                    sessionCatalogue,
                    ReadCatalogue(applicationConnectionString!),
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{SectionName}:{nameof(ConnectionString)} must name a catalogue other than the DotNetNuke database.")
                    + " The existing schema is immutable, so session state is held outside it. The catalogue"
                    + " NAME is compared rather than the whole connection string, because a host is spellable"
                    + " many ways and a rule that compared hosts could be bypassed by spelling one"
                    + " differently.");
            }

            if (!string.IsNullOrWhiteSpace(applicationConnectionString)
                && ReadCatalogue(applicationConnectionString!).Length == 0)
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"ConnectionStrings:Default must name its catalogue explicitly for {SectionName}:{nameof(ConnectionString)} to be accepted.")
                    + " The session catalogue is required to differ from the application's, and a connection"
                    + " string that names no catalogue makes that impossible to establish.");
            }
        }

        return failures;
    }

    /// <summary>Reports whether a catalogue name is one of SQL Server's own administrative databases.</summary>
    /// <param name="catalogue">The catalogue name read from a connection string.</param>
    /// <returns><see langword="true"/> when session state must not be held there.</returns>
    /// <remarks>
    /// The four are fixed by the product rather than by this application, so the set is closed. <c>tempdb</c>
    /// earns its place twice over: a table created there does not survive a restart, so a deployment naming it
    /// would believe it had durable sessions and have process-local ones under a durable provider's name.
    /// </remarks>
    private static bool IsSystemCatalogue(string catalogue) =>
        string.Equals(catalogue, "master", StringComparison.OrdinalIgnoreCase)
        || string.Equals(catalogue, "model", StringComparison.OrdinalIgnoreCase)
        || string.Equals(catalogue, "msdb", StringComparison.OrdinalIgnoreCase)
        || string.Equals(catalogue, "tempdb", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bounds a value read from configuration before it is quoted into a start-up message.</summary>
    /// <param name="value">The value to render.</param>
    /// <returns>The value, truncated when it is longer than a plausible identifier.</returns>
    /// <remarks>
    /// A catalogue name is not a secret, but it is operator-supplied text that ends up in a start-up log, so
    /// it is length-bounded for the same reason <see cref="QuoteProvider"/> bounds the provider name.
    /// </remarks>
    private static string Redact(string value) => value.Length <= MaximumQuotedProviderLength
        ? value
        : value[..MaximumQuotedProviderLength];

    /// <summary>Compares the configured provider with one of the accepted names.</summary>
    /// <param name="candidate">The accepted name to compare against.</param>
    /// <returns><see langword="true"/> when the configured value names it.</returns>
    private bool MatchesProvider(string candidate) => string.Equals(
        (Provider ?? string.Empty).Trim(),
        candidate,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>Renders the configured provider for a validation message, bounded in length.</summary>
    /// <returns>The quoted value, or the word describing its absence.</returns>
    private string QuoteProvider()
    {
        string configured = (Provider ?? string.Empty).Trim();

        if (configured.Length == 0)
        {
            return "empty";
        }

        return configured.Length <= MaximumQuotedProviderLength
            ? $"'{configured}'"
            : $"'{configured[..MaximumQuotedProviderLength]}' (truncated)";
    }

    /// <summary>Reads the catalogue a connection string names.</summary>
    /// <param name="connectionString">The connection string to read.</param>
    /// <returns>The catalogue it names, or the empty string when it names none.</returns>
    /// <remarks>
    /// <para>
    /// Both spellings the provider accepts are read, because a rule that recognised only one of them could be
    /// bypassed by writing the other. The keywords are matched case-insensitively and the value is trimmed,
    /// which is what the provider itself does.
    /// </para>
    /// <para>
    /// MIGRATION: SEC-07. This used to read the SERVER as well, and the isolation rule compared both. The
    /// server is deliberately no longer read: a host has too many equivalent spellings for a textual
    /// comparison to be sound, and including one in the rule made the rule weaker rather than stronger,
    /// because two spellings of one host read as two different servers and the refusal never fired. The rule
    /// that replaced it needs the catalogue alone.
    /// </para>
    /// <para>
    /// Written by hand rather than with a connection-string builder because this type is dependency-free by
    /// design: the Application project references the Domain project and nothing else, so the provider's own
    /// parser - which lives in the client library the Infrastructure project owns - is not reachable from
    /// here.
    /// </para>
    /// </remarks>
    private static string ReadCatalogue(string connectionString)
    {
        string catalogue = string.Empty;

        foreach (string pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            string keyword = pair[..separator].Trim();

            if (keyword.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
            {
                catalogue = pair[(separator + 1)..].Trim();
            }
        }

        return catalogue;
    }

    /// <summary>Reports whether a value is a plain, unquoted SQL identifier.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns><see langword="true"/> when it is safe to bracket-quote and emit.</returns>
    private static bool IsPlainIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return char.IsLetter(value[0]) || value[0] == '_';
    }

    /// <summary>Escapes a closing bracket so an identifier can be bracket-quoted safely.</summary>
    /// <param name="value">The identifier to escape.</param>
    /// <returns>The escaped identifier.</returns>
    private static string Quote(string? value) =>
        (value ?? string.Empty).Replace("]", "]]", StringComparison.Ordinal);
}
