// This type has NO legacy counterpart, because the legacy application had no refresh-token concept at all.
namespace DnnMigration.Application.Options;

/// <summary>
/// Strongly typed configuration describing which refresh-token store a deployment runs and how large the
/// in-process store is allowed to grow, bound from the configuration section named by <see
/// cref="SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is a deliberately minimal, dependency-free plain object carrying primitives and no attributes,
/// for the reason recorded on every options class in this folder: the Application project references the
/// Domain project and nothing else, so nothing declared here may reach the configuration, hosting or
/// dependency-injection abstractions that perform the binding.
/// </para>
/// <para>
/// <strong>The accepted providers are a closed set, and one of them is a DECLARATION rather than an
/// implementation.</strong> <see cref="InProcessProvider"/> - or its alias <see cref="InMemoryProvider"/> -
/// selects the process-local store this solution ships. <see cref="SqlServerProvider"/> selects the shared,
/// durable store it also ships, which holds one table in a catalogue of its own and is therefore
/// replica-safe and restart-surviving; it is opt-in and defaulted off, so this repository alone remains a
/// working deployment without it. <see cref="ExternalProvider"/> asserts that the deployment has registered
/// its own <c>IRefreshTokenStore</c> after <c>AddInfrastructure</c> - the last registration of a service
/// wins, so no change to this repository's Infrastructure layer is required to substitute one - and the
/// host verifies that assertion while it starts.
/// </para>
/// </remarks>
public sealed class RefreshTokenStoreOptions
{
    /// <summary>Name of the configuration section this class binds from.</summary>
    /// <remarks>
    /// Deliberately a section of its own rather than three more keys under <c>Jwt</c>. The refresh-token
    /// LIFETIMES - <c>Jwt:RefreshTokenExpirationDays</c> and <c>Jwt:RefreshTokenAbsoluteExpirationDays</c>
    /// - are token policy, stamped by the token service and read by the store, so they belong beside the
    /// signing settings.
    /// </remarks>
    public const string SectionName = "RefreshTokenStore";

    /// <summary>Provider name selecting the process-local store this solution ships: <c>InProcess</c>.</summary>
    public const string InProcessProvider = "InProcess";

    /// <summary>Provider name declaring that the deployment supplies its own store: <c>External</c>.</summary>
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
    /// OPT-IN AND DEFAULTED OFF, and it is the only accepted value under which refresh state survives a
    /// restart and is observed identically by every replica. It selects <c>SqlServerRefreshTokenStore</c>,
    /// which holds one table of its own in a catalogue of its own.
    /// </remarks>
    public const string SqlServerProvider = "SqlServer";

    /// <summary>Accepted alias for <see cref="InProcessProvider"/>: <c>InMemory</c>.</summary>
    public const string InMemoryProvider = "InMemory";

    /// <summary>Schema the shared store's table is created in when none is configured: <c>dbo</c>.</summary>
    public const string DefaultSchema = "dbo";

    /// <summary>Name of the shared store's table when none is configured.</summary>
    public const string DefaultTableName = "DnnMigrationRefreshTokens";

    /// <summary>Smallest tracked-generation ceiling a deployment may configure: <c>1000</c>.</summary>
    public const int MinimumTrackedTokenCeiling = 1_000;

    /// <summary>Largest tracked-generation ceiling a deployment may configure: <c>1000000</c>.</summary>
    public const int MaximumTrackedTokenCeiling = 1_000_000;

    /// <summary>Longest same-client concurrent-use grace a deployment may configure, in seconds: <c>60</c>.</summary>
    /// <remarks>
    /// The grace is the window in which a second presentation of an already-spent refresh token by the SAME
    /// client fingerprint is treated as a near-simultaneous retry rather than as a theft. It exists because
    /// a client whose first exchange was interrupted mid-flight would otherwise have its whole family
    /// revoked.
    /// </remarks>
    public const int MaximumConcurrentUseGraceSeconds = 60;

    /// <summary>
    /// Shortest retention a deployment may configure for a revoked refresh record, in hours: <c>1</c>.
    /// </summary>
    /// <remarks>
    /// PRIV-02. A revoked record keeps its token digest so that a later presentation of that family is
    /// recognisable as a replay rather than as an unknown value; erasing it the instant a sign-out
    /// completes would discard the one signal that makes credential theft visible after the fact.
    /// </remarks>
    public const int MinimumRevokedRetentionHours = 1;

    /// <summary>
    /// Longest retention a deployment may configure for a revoked refresh record, in hours: <c>720</c>
    /// (thirty days).
    /// </summary>
    /// <remarks>
    /// PRIV-02. The upper bound is a data-minimisation limit rather than a technical one.
    /// </remarks>
    public const int MaximumRevokedRetentionHours = 720;

    /// <summary>
    /// Shortest interval between reclamation sweeps a deployment may configure, in minutes: <c>1</c>.
    /// </summary>
    public const int MinimumRetentionSweepMinutes = 1;

    /// <summary>
    /// Longest interval between reclamation sweeps a deployment may configure, in minutes: <c>1440</c> (one
    /// day).
    /// </summary>
    public const int MaximumRetentionSweepMinutes = 1_440;

    /// <summary>
    /// Longest description of a configured provider that a validation failure will quote: <c>64</c>
    /// characters.
    /// </summary>
    private const int MaximumQuotedProviderLength = 64;

    /// <summary>Gets or sets the name of the refresh-token store this deployment runs.</summary>
    /// <value>
    /// <see cref="InProcessProvider"/> or <see cref="ExternalProvider"/>, compared without regard to case
    /// or surrounding whitespace.
    /// </value>
    public string Provider { get; set; } = InProcessProvider;

    /// <summary>Gets or sets how many refresh-token generations the in-process store tracks at once.</summary>
    /// <value>
    /// A ceiling between <see cref="MinimumTrackedTokenCeiling"/> and <see
    /// cref="MaximumTrackedTokenCeiling"/> inclusive.
    /// </value>
    /// <remarks>
    /// A bound is mandatory rather than defensive: nothing leaves the store before its family's absolute
    /// ceiling passes, so without one a caller holding valid credentials could grow the tracked set
    /// indefinitely.
    /// </remarks>
    public int MaximumTrackedTokens { get; set; } = 100_000;

    /// <summary>Gets or sets the same-client concurrent-use grace, in seconds.</summary>
    /// <value>
    /// Zero to <see cref="MaximumConcurrentUseGraceSeconds"/> inclusive, where zero disables the grace and
    /// makes any second presentation of a spent token a replay.
    /// </value>
    /// <remarks>
    /// Zero is permitted and is the strictest setting: it treats every reuse as theft and revokes the
    /// family. It is not the default because an interrupted exchange - a dropped response, a retried
    /// request - is a real occurrence that would otherwise sign the caller out, and the fingerprint
    /// comparison already confines the forgiveness to the client that spent the token.
    /// </remarks>
    public int ConcurrentUseGraceSeconds { get; set; } = 5;

    /// <summary>Gets or sets how long a REVOKED refresh record is retained before it is erased, in hours.</summary>
    /// <value>
    /// <see cref="MinimumRevokedRetentionHours"/> to <see cref="MaximumRevokedRetentionHours"/> inclusive.
    /// </value>
    /// <remarks>
    /// Twenty-four hours is the default because the record's only remaining purpose is the replay signal: a
    /// stolen credential presented after a sign-out is what a retained digest makes recognisable, and that
    /// attempt arrives within minutes or hours of the theft rather than days. Keeping it longer retains
    /// personal data for a signal nobody will read.
    /// </remarks>
    public int RevokedRecordRetentionHours { get; set; } = 24;

    /// <summary>Gets or sets how often the operation-independent reclamation sweep runs, in minutes.</summary>
    /// <value>
    /// <see cref="MinimumRetentionSweepMinutes"/> to <see cref="MaximumRetentionSweepMinutes"/> inclusive.
    /// </value>
    public int RetentionSweepMinutes { get; set; } = 60;

    /// <summary>Gets or sets the connection string of the catalogue holding the shared store's table.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Gets or sets the schema holding the shared store's table.</summary>
    public string Schema { get; set; } = DefaultSchema;

    /// <summary>Gets or sets the name of the shared store's table.</summary>
    public string TableName { get; set; } = DefaultTableName;

    /// <summary>
    /// Name of the setting this class no longer carries, retained so the host can refuse a configuration
    /// that still sets it rather than binding it to nothing: <c>CreateTableIfMissing</c>.
    /// </summary>
    public const string RemovedCreateTableSetting = "CreateTableIfMissing";

    /// <summary>Gets or sets the command timeout, in seconds, the shared store issues its statements with.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets a value indicating whether the deployment states, on the record, that it runs exactly
    /// ONE API instance and therefore accepts a refresh-token store that is neither shared between replicas
    /// nor carried across a restart.
    /// </summary>
    /// <remarks>
    /// <strong>SO IN PRODUCTION THE HOST NOW REFUSES TO START unless one of two things is true:</strong>
    /// the active store is authoritative across replicas - <see cref="SqlServerProvider"/>, or a
    /// deployment-supplied store that reports itself so - or this acknowledgement is set.
    /// </remarks>
    public bool AcknowledgeSingleInstance { get; set; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Provider"/> names the process-local store this solution
    /// ships.
    /// </summary>
    public bool UsesInProcessStore =>
        MatchesProvider(InProcessProvider) || MatchesProvider(InMemoryProvider);

    /// <summary>
    /// Gets a value indicating whether <see cref="Provider"/> declares a deployment-supplied store.
    /// </summary>
    public bool UsesExternalStore => MatchesProvider(ExternalProvider);

    /// <summary>
    /// Gets a value indicating whether the configured provider selects this solution's shared, durable
    /// store.
    /// </summary>
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
    /// The rules here are the ones this object can judge alone: that the provider is a name this build
    /// recognises, and that neither number is outside the range in which the store can function at all. The
    /// narrower operational bounds - the floor and ceiling on tracked generations and the ceiling on the
    /// grace - are POLICY, so the Api layer's validator adds them alongside this call.
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
    /// The four are fixed by the product rather than by this application, so the set is closed.
    /// <c>tempdb</c> earns its place twice over: a table created there does not survive a restart, so a
    /// deployment naming it would believe it had durable sessions and have process-local ones under a
    /// durable provider's name.
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
    /// A catalogue name is not a secret, but it is operator-supplied text that ends up in a start-up log,
    /// so it is length-bounded for the same reason <see cref="QuoteProvider"/> bounds the provider name.
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
