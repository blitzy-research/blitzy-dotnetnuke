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
    /// which holds one table of its own in a catalogue of its own, created on first use unless
    /// <see cref="CreateTableIfMissing"/> is cleared.
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
    /// Gets or sets a value indicating whether the shared store creates its table when it is absent.
    /// </summary>
    /// <remarks>
    /// Applies only to the store's OWN catalogue, never to the DotNetNuke database, which
    /// <see cref="Validate"/> refuses outright. Clear it where schema changes are applied by a separate
    /// deployment step, and provision the table by hand.
    /// </remarks>
    public bool CreateTableIfMissing { get; set; } = true;

    /// <summary>Gets or sets the command timeout, in seconds, the shared store issues its statements with.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;

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

        // ⚠ THE ONE CHECK THAT KEEPS THE DURABLE OPTION INSIDE RULE T4. Nothing is created in the
        // DotNetNuke catalogue, so a shared store pointed at it is refused before the host starts rather
        // than discovered when the first sign-in tries to create a table there.
        if (!string.IsNullOrWhiteSpace(ConnectionString)
            && !string.IsNullOrWhiteSpace(applicationConnectionString)
            && SharesCatalogue(ConnectionString!, applicationConnectionString!))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"{SectionName}:{nameof(ConnectionString)} must name a catalogue other than the DotNetNuke database.")
                + " The existing schema is immutable, so session state is held outside it.");
        }

        return failures;
    }

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

    /// <summary>Reports whether two connection strings name the same server and catalogue.</summary>
    /// <param name="first">One connection string.</param>
    /// <param name="second">The other connection string.</param>
    /// <returns><see langword="true"/> when both name the same catalogue on the same server.</returns>
    private static bool SharesCatalogue(string first, string second)
    {
        (string firstServer, string firstCatalogue) = ReadTarget(first);
        (string secondServer, string secondCatalogue) = ReadTarget(second);

        if (firstCatalogue.Length == 0 || secondCatalogue.Length == 0)
        {
            return false;
        }

        return string.Equals(firstCatalogue, secondCatalogue, StringComparison.OrdinalIgnoreCase)
            && string.Equals(firstServer, secondServer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the server and catalogue keywords out of a connection string.</summary>
    /// <param name="connectionString">The connection string to read.</param>
    /// <returns>The server and catalogue it names, each empty when it names none.</returns>
    private static (string Server, string Catalogue) ReadTarget(string connectionString)
    {
        string server = string.Empty;
        string catalogue = string.Empty;

        foreach (string pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            string keyword = pair[..separator].Trim();
            string value = pair[(separator + 1)..].Trim();

            if (keyword.Equals("Server", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("Address", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("Addr", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("Network Address", StringComparison.OrdinalIgnoreCase))
            {
                server = value;
            }
            else if (keyword.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
            {
                catalogue = value;
            }
        }

        return (server, catalogue);
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
