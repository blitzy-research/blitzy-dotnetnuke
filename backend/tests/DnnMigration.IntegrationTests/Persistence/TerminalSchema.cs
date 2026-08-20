using System.Globalization;
using System.Reflection;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// The terminal shape of the mapped DotNetNuke tables, read from <c>Schema/TerminalSchema.manifest</c>.
/// </summary>
/// <remarks>
/// The manifest's own header records how it was derived, which legacy statement establishes each record,
/// and what it deliberately does not assert. It also records the two drifted declarations that measuring
/// against it found on the first run.
/// </remarks>
internal static class TerminalSchema
{
    /// <summary>The embedded resource carrying the manifest.</summary>
    private const string ResourceName = "DnnMigration.IntegrationTests.Schema.TerminalSchema.manifest";

    /// <summary>The token a manifest field uses for "none".</summary>
    private const string AbsentToken = "-";

    private static readonly Lazy<TerminalSchemaDocument> Document = new(Load, isThreadSafe: true);

    /// <summary>Gets every mapped table, keyed by its legacy table name.</summary>
    public static IReadOnlyDictionary<string, TerminalTable> Tables => Document.Value.Tables;

    /// <summary>Gets every mapped column, keyed <c>Table.Column</c> with the legacy spelling.</summary>
    public static IReadOnlyDictionary<string, TerminalColumn> Columns => Document.Value.Columns;

    /// <summary>Gets every terminal non-primary index, keyed by its physical name.</summary>
    public static IReadOnlyDictionary<string, TerminalIndex> Indexes => Document.Value.Indexes;

    /// <summary>Gets every terminal physical foreign key, keyed by its constraint name.</summary>
    public static IReadOnlyDictionary<string, TerminalForeignKey> ForeignKeys => Document.Value.ForeignKeys;

    /// <summary>Gets the counts the manifest declares for itself in its <c>TOTALS</c> record.</summary>
    public static TerminalSchemaTotals DeclaredTotals => Document.Value.Totals;

    /// <summary>Returns the columns of one table, in name order.</summary>
    /// <param name="table">The legacy table name.</param>
    /// <returns>The table's columns.</returns>
    public static IReadOnlyList<TerminalColumn> ColumnsOf(string table) =>
        Columns.Values
            .Where(column => string.Equals(column.Table, table, StringComparison.Ordinal))
            .OrderBy(column => column.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Reads and parses the embedded manifest.</summary>
    /// <returns>The parsed document.</returns>
    /// <exception cref="InvalidOperationException">The resource is missing or malformed.</exception>
    private static TerminalSchemaDocument Load()
    {
        Assembly assembly = typeof(TerminalSchema).Assembly;

        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"The terminal-schema manifest '{ResourceName}' is not embedded in {assembly.GetName().Name}.")
                + " Every schema comparison depends on it, so a missing resource is a build defect rather"
                + " than a skippable condition.");

        using StreamReader reader = new(stream);

        var tables = new Dictionary<string, TerminalTable>(StringComparer.Ordinal);
        var columns = new Dictionary<string, TerminalColumn>(StringComparer.Ordinal);
        var indexes = new Dictionary<string, TerminalIndex>(StringComparer.Ordinal);
        var foreignKeys = new Dictionary<string, TerminalForeignKey>(StringComparer.Ordinal);
        TerminalSchemaTotals? declared = null;

        int lineNumber = 0;

        while (reader.ReadLine() is string line)
        {
            lineNumber++;
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            string[] fields = trimmed.Split('|').Select(field => field.Trim()).ToArray();

            switch (fields[0])
            {
                case "TABLE":
                    Expect(fields, 6, lineNumber, trimmed);
                    Add(tables, fields[1], new TerminalTable(
                        fields[1],
                        fields[2],
                        SplitColumns(fields[3]),
                        IsClustered(fields[4], lineNumber, trimmed),
                        fields[5]), lineNumber, trimmed);
                    break;

                case "COLUMN":
                    Expect(fields, 8, lineNumber, trimmed);
                    TerminalColumn column = new(
                        fields[1],
                        fields[2],
                        fields[3],
                        Optional(fields[4], lineNumber, trimmed),
                        Nullability(fields[5], lineNumber, trimmed),
                        Identity(fields[6], lineNumber, trimmed),
                        fields[7]);
                    Add(columns, column.Key, column, lineNumber, trimmed);
                    break;

                case "INDEX":
                    Expect(fields, 6, lineNumber, trimmed);
                    Add(indexes, fields[1], new TerminalIndex(
                        fields[1],
                        fields[2],
                        SplitColumns(fields[3]),
                        Uniqueness(fields[4], lineNumber, trimmed),
                        fields[5]), lineNumber, trimmed);
                    break;

                case "FOREIGNKEY":
                    Expect(fields, 8, lineNumber, trimmed);
                    Add(foreignKeys, fields[1], new TerminalForeignKey(
                        fields[1],
                        fields[2],
                        SplitColumns(fields[3]),
                        fields[4],
                        SplitColumns(fields[5]),
                        DeleteAction(fields[6], lineNumber, trimmed),
                        fields[7]), lineNumber, trimmed);
                    break;

                case "TOTALS":
                    Expect(fields, 5, lineNumber, trimmed);
                    declared = new TerminalSchemaTotals(
                        Count(fields[1], "tables", lineNumber, trimmed),
                        Count(fields[2], "columns", lineNumber, trimmed),
                        Count(fields[3], "indexes", lineNumber, trimmed),
                        Count(fields[4], "foreignkeys", lineNumber, trimmed));
                    break;

                default:
                    throw Malformed(lineNumber, trimmed, FormattableString.Invariant(
                        $"'{fields[0]}' is not a manifest record type."));
            }
        }

        if (declared is null)
        {
            throw new InvalidOperationException(
                "The terminal-schema manifest carries no TOTALS record, so a truncated resource could not "
                + "be distinguished from a complete one.");
        }

        if (declared.Tables != tables.Count
            || declared.Columns != columns.Count
            || declared.Indexes != indexes.Count
            || declared.ForeignKeys != foreignKeys.Count)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"The terminal-schema manifest declares {declared.Tables} tables, {declared.Columns} columns, {declared.Indexes} indexes and {declared.ForeignKeys} foreign keys, but {tables.Count}, {columns.Count}, {indexes.Count} and {foreignKeys.Count} were parsed.")
                + " The resource is truncated or a record was rejected.");
        }

        return new TerminalSchemaDocument(tables, columns, indexes, foreignKeys, declared);
    }

    /// <summary>Splits a comma-separated column list, preserving order.</summary>
    /// <param name="field">The raw field.</param>
    /// <returns>The column names, in declared order.</returns>
    private static IReadOnlyList<string> SplitColumns(string field) =>
        field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Reads an optional integer field.</summary>
    /// <param name="field">The raw field.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    /// <returns>The value, or <see langword="null"/> when the field is the absence token.</returns>
    private static int? Optional(string field, int lineNumber, string line) =>
        string.Equals(field, AbsentToken, StringComparison.Ordinal)
            ? null
            : int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : throw Malformed(lineNumber, line, FormattableString.Invariant(
                    $"'{field}' is neither an integer nor '{AbsentToken}'."));

    /// <summary>Reads a nullability field.</summary>
    /// <param name="field">The raw field.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    /// <returns><see langword="true"/> when the column admits nulls.</returns>
    private static bool Nullability(string field, int lineNumber, string line) => field switch
    {
        "NULL" => true,
        "NOT NULL" => false,
        _ => throw Malformed(lineNumber, line, FormattableString.Invariant(
            $"'{field}' is neither 'NULL' nor 'NOT NULL'.")),
    };

    /// <summary>Reads an identity field of the form <c>seed,increment</c>.</summary>
    /// <param name="field">The raw field.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    /// <returns>The seed and increment, or <see langword="null"/> for a non-identity column.</returns>
    private static (int Seed, int Increment)? Identity(string field, int lineNumber, string line)
    {
        if (string.Equals(field, AbsentToken, StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = field.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int increment))
        {
            throw Malformed(lineNumber, line, FormattableString.Invariant(
                $"'{field}' is not an identity specification of the form seed,increment."));
        }

        return (seed, increment);
    }

    /// <summary>Reads an index uniqueness field.</summary>
    /// <param name="field">The raw field.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    /// <returns><see langword="true"/> for a unique index.</returns>
    private static bool Uniqueness(string field, int lineNumber, string line) => field switch
    {
        "UNIQUE" => true,
        "INDEX" => false,
        _ => throw Malformed(lineNumber, line, FormattableString.Invariant(
            $"'{field}' is neither 'UNIQUE' nor 'INDEX'.")),
    };

    /// <summary>Reads a primary-key topology field.</summary>
    /// <param name="field">The raw field.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    /// <returns><see langword="true"/> for a clustered key.</returns>
    private static bool IsClustered(string field, int lineNumber, string line) => field switch
    {
        "CLUSTERED" => true,
        "NONCLUSTERED" => false,
        _ => throw Malformed(lineNumber, line, FormattableString.Invariant(
            $"'{field}' is neither 'CLUSTERED' nor 'NONCLUSTERED'.")),
    };

    /// <summary>Reads a delete-action field and normalises it to the catalogue vocabulary.</summary>
    /// <param name="field">The raw field.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    /// <returns>The action as <c>sys.foreign_keys</c> spells it.</returns>
    private static string DeleteAction(string field, int lineNumber, string line) => field switch
    {
        "CASCADE" => "CASCADE",
        "NO ACTION" => "NO_ACTION",
        _ => throw Malformed(lineNumber, line, FormattableString.Invariant(
            $"'{field}' is neither 'CASCADE' nor 'NO ACTION'.")),
    };

    /// <summary>Reads one <c>name count</c> pair from the totals record.</summary>
    /// <param name="field">The raw field.</param>
    /// <param name="expectedName">The name the field must carry.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    /// <returns>The declared count.</returns>
    private static int Count(string field, string expectedName, int lineNumber, string line)
    {
        string[] parts = field.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2
            || !string.Equals(parts[0], expectedName, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw Malformed(lineNumber, line, FormattableString.Invariant(
                $"'{field}' is not the expected '{expectedName} <count>' pair."));
        }

        return value;
    }

    /// <summary>Refuses a record whose field count is wrong.</summary>
    /// <param name="fields">The parsed fields.</param>
    /// <param name="expected">The field count the record type requires.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    private static void Expect(string[] fields, int expected, int lineNumber, string line)
    {
        if (fields.Length != expected)
        {
            throw Malformed(lineNumber, line, FormattableString.Invariant(
                $"a {fields[0]} record carries {expected} fields, not {fields.Length}."));
        }
    }

    /// <summary>Adds a record, refusing a duplicate key.</summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="target">The accumulating dictionary.</param>
    /// <param name="key">The record key.</param>
    /// <param name="value">The record.</param>
    /// <param name="lineNumber">The manifest line, for diagnosis.</param>
    /// <param name="line">The manifest line text, for diagnosis.</param>
    private static void Add<T>(Dictionary<string, T> target, string key, T value, int lineNumber, string line)
    {
        if (!target.TryAdd(key, value))
        {
            throw Malformed(lineNumber, line, FormattableString.Invariant($"'{key}' is declared twice."));
        }
    }

    /// <summary>Builds the exception raised for every malformed record.</summary>
    /// <param name="lineNumber">The manifest line.</param>
    /// <param name="line">The manifest line text.</param>
    /// <param name="reason">What is wrong with it.</param>
    /// <returns>The exception to raise.</returns>
    private static InvalidOperationException Malformed(int lineNumber, string line, string reason) =>
        new(FormattableString.Invariant(
            $"TerminalSchema.manifest line {lineNumber} is malformed: {reason} The line reads: {line}"));

    /// <summary>The parsed manifest.</summary>
    /// <param name="Tables">Every mapped table, keyed by name.</param>
    /// <param name="Columns">Every mapped column, keyed <c>Table.Column</c>.</param>
    /// <param name="Indexes">Every non-primary index, keyed by name.</param>
    /// <param name="ForeignKeys">Every physical foreign key, keyed by name.</param>
    /// <param name="Totals">The counts the manifest declares for itself.</param>
    private sealed record TerminalSchemaDocument(
        IReadOnlyDictionary<string, TerminalTable> Tables,
        IReadOnlyDictionary<string, TerminalColumn> Columns,
        IReadOnlyDictionary<string, TerminalIndex> Indexes,
        IReadOnlyDictionary<string, TerminalForeignKey> ForeignKeys,
        TerminalSchemaTotals Totals);
}

/// <summary>One mapped table as the terminal legacy schema declares it.</summary>
/// <param name="Name">The legacy table name.</param>
/// <param name="PrimaryKeyName">The primary-key constraint name.</param>
/// <param name="PrimaryKeyColumns">The key columns, in key order.</param>
/// <param name="IsClustered">Whether the key is clustered.</param>
/// <param name="Provenance">The legacy script and line that established the key.</param>
internal sealed record TerminalTable(
    string Name,
    string PrimaryKeyName,
    IReadOnlyList<string> PrimaryKeyColumns,
    bool IsClustered,
    string Provenance);

/// <summary>One mapped column as the terminal legacy schema declares it.</summary>
/// <param name="Table">The legacy table name.</param>
/// <param name="Name">The legacy column name, with its original casing.</param>
/// <param name="DataType">The store type name, lower-cased, without any length specifier.</param>
/// <param name="MaxLength">
/// The declared length in characters, or <see langword="null"/> for a type without one.
/// </param>
/// <param name="IsNullable">Whether the column admits nulls.</param>
/// <param name="Identity">The identity seed and increment, or <see langword="null"/>.</param>
/// <param name="Provenance">The legacy script and line that established this shape.</param>
internal sealed record TerminalColumn(
    string Table,
    string Name,
    string DataType,
    int? MaxLength,
    bool IsNullable,
    (int Seed, int Increment)? Identity,
    string Provenance)
{
    /// <summary>Gets the <c>Table.Column</c> identity used as the comparison key everywhere.</summary>
    public string Key => FormattableString.Invariant($"{Table}.{Name}");

    /// <summary>Renders the declaration the way the legacy script spells it, for failure messages.</summary>
    /// <returns>A single-line declaration.</returns>
    public string Declaration() => FormattableString.Invariant(
        $"{DataType}{(MaxLength is int length ? $"({length})" : string.Empty)} {(IsNullable ? "NULL" : "NOT NULL")}{(Identity is { } identity ? $" IDENTITY({identity.Seed},{identity.Increment})" : string.Empty)}");
}

/// <summary>One terminal non-primary index.</summary>
/// <param name="Name">The physical index name.</param>
/// <param name="Table">The table it covers.</param>
/// <param name="Columns">The key columns, in index order.</param>
/// <param name="IsUnique">Whether the index is unique.</param>
/// <param name="Provenance">The legacy script and line that created it.</param>
internal sealed record TerminalIndex(
    string Name,
    string Table,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    string Provenance);

/// <summary>One terminal physical foreign key.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Table">The dependent table.</param>
/// <param name="Columns">The dependent columns, in constraint order.</param>
/// <param name="PrincipalTable">The principal table.</param>
/// <param name="PrincipalColumns">The principal columns, in constraint order.</param>
/// <param name="DeleteAction">The delete action, spelled as <c>sys.foreign_keys</c> spells it.</param>
/// <param name="Provenance">The legacy script and line that created it.</param>
internal sealed record TerminalForeignKey(
    string Name,
    string Table,
    IReadOnlyList<string> Columns,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumns,
    string DeleteAction,
    string Provenance);

/// <summary>The counts the manifest declares for itself.</summary>
/// <param name="Tables">The declared table count.</param>
/// <param name="Columns">The declared column count.</param>
/// <param name="Indexes">The declared index count.</param>
/// <param name="ForeignKeys">The declared foreign-key count.</param>
internal sealed record TerminalSchemaTotals(int Tables, int Columns, int Indexes, int ForeignKeys);
