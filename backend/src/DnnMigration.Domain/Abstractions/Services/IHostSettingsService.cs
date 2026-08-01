namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Reads and writes the installation-wide configuration rows that the legacy
/// platform persisted in the <c>HostSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately narrow.</b> This contract stands in for the reachable surface
/// of two excluded legacy subsystems: the <c>DotNetNuke.Common.Globals</c>
/// module, whose source is the 2,704-line
/// <c>Library/Components/Shared/Globals.vb</c> and which 111 files referenced,
/// and the three-file <c>DotNetNuke.Entities.Host</c> namespace, which 53 files
/// referenced. Measured coupling from the code actually being migrated is only
/// six distinct members across fourteen call sites, and this interface covers
/// exactly the <c>HostSettings</c>-table portion of those six. The other five
/// are placed elsewhere and are itemised in the migration annotations below, so
/// that neither excluded module is reconstructed here.
/// </para>
/// <para>
/// <b>A real table, not per-request state.</b> The rows behind these members
/// live in a genuine relational table and are shared by every portal in the
/// installation, which is what separates this abstraction from
/// <c>IPortalContext</c>. That sibling covers the per-request composite the
/// legacy code assembled and mutated for the lifetime of a single request;
/// these values outlive any request.
/// </para>
/// <para>
/// <b>Typed, materialised reads and one upsert.</b> The legacy shapes are
/// deliberately gone. Each legacy read handed back a forward-only data-reader,
/// and the whole-table read handed back an untyped, mutable, pre-generics
/// collection, leaving every caller to iterate, coerce and dispose. Here a
/// single lookup is answered by a nullable string, the whole-table lookup is
/// answered by a fully materialised read-only projection, and the write is an
/// upsert.
/// </para>
/// <para>
/// <b>Absent means <see langword="null"/>.</b> A key with no matching row is
/// reported as <see langword="null"/> rather than as the legacy empty string.
/// Domain contracts carry nullable CLR types; where an externally visible
/// contract still depends on the empty-string form, the DTO and API boundary
/// restores it explicitly there instead of encoding a sentinel into this
/// interface.
/// </para>
/// <para>
/// <b>Implementor.</b> <c>DnnMigration.Infrastructure</c> supplies the single
/// implementation, in <c>Services/HostSettingsService.cs</c>. It alone holds the
/// persistence context, and it alone performs the stored-copy discard that the
/// legacy write performed as its final step. Neither concern is expressible
/// through this contract, and neither belongs on it.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// string? storage = await hostSettings.GetSettingAsync("SiteLogStorage", token);
/// await hostSettings.UpsertSettingAsync("SiteLogStorage", "D", false, token);
/// </code>
/// </example>
public interface IHostSettingsService
{
    // MIGRATION: The legacy read and write shapes are not carried forward. The
    // abstract data surface answered both host reads with a forward-only
    // data-reader (Library/Components/Providers/Data/DataProvider.vb L87 and
    // L88), and both materialising readers plus the Globals facade answered the
    // whole-table read with an untyped, mutable, pre-generics collection
    // (Library/Components/Host/HostSettings.vb L47-L70 and
    // Library/Components/Shared/Globals.vb L221-L225). The former type is
    // unavailable to this project by design, because the Domain layer takes no
    // reference of any kind; the latter is replaced by the generic primitive
    // IReadOnlyDictionary<string, string>, which is immutable to its caller and
    // fully typed.

    // MIGRATION: A synchronous property that performed a database read becomes an
    // awaitable method. Library/Components/Shared/Globals.vb L221-L225 declared
    // "Public ReadOnly Property HostSettings()" and reached storage from inside
    // its getter, so every one of the twenty-one call sites that touched it
    // issued blocking I/O through what looked like a field access. A property can
    // neither be awaited nor accept a cancellation signal, so the whole-table
    // read is exposed here as the method GetSettingsAsync, and every member on
    // this contract is asynchronous and cancellable.

    // MIGRATION: The write side effect belongs to the implementor, not to this
    // contract. The legacy upsert finished by discarding the host-wide stored
    // copy (Library/Components/Host/HostSettingsController.vb L57), a storage
    // concern rather than a contract concern, so this interface declares no
    // eviction member, no key name and no timeout. The Infrastructure
    // implementation performs that step, alongside the sole persistence context.

    // MIGRATION: Five of the six legacy members reached from migrated code are
    // deliberately absent, because reconstructing the excluded Globals module was
    // never the intent; each has its own named home. The runtime performance
    // multiplier (Library/Components/Shared/Globals.vb L227-L231, which silently
    // fell back to 3 whenever its row was missing) becomes a bound options class
    // at Application/Options/CachingOptions.cs. The application virtual path and
    // the two physical-path members become host-environment and link-generation
    // services in the API layer. The unauthenticated-user role name becomes a
    // domain constant owned elsewhere. The server-name and web-farm members are
    // not reached from migrated code at all. Also absent is the misleadingly
    // named secure-settings reader (Library/Components/Host/HostSettings.vb
    // L72-L95), which in fact answers with only the NON-secure rows and
    // additionally drops any name containing "password": it served the excluded
    // host-level administration screens, and per Minimal Change Clause item 1 its
    // naming defect is recorded here rather than corrected.

    // MIGRATION: A key with no matching row now yields null. The legacy reader at
    // Library/Components/Host/HostSettings.vb L39-L45 answered a missing key with
    // the empty-string sentinel (Null.NullString,
    // Library/Components/Shared/Null.vb L71-L75), and hydration mapped a database
    // NULL to the empty string as well (Library/Components/Host/HostSettings.vb
    // L56-L60), so an absent row, a NULL column and a genuinely empty value were
    // indistinguishable to every caller. Domain contracts use nullable CLR types
    // instead; where an externally visible contract still depends on the
    // empty-string form, the DTO and API boundary preserves it explicitly.

    /// <summary>
    /// Retrieves the value of a single host setting.
    /// </summary>
    /// <param name="settingName">
    /// Name of the setting to look up, matched against the setting-name column
    /// exactly as the legacy schema stores it.
    /// </param>
    /// <param name="cancellationToken">Signals that the lookup should be abandoned.</param>
    /// <returns>
    /// A task whose completed value is the stored setting value, or
    /// <see langword="null"/> when no row carries
    /// <paramref name="settingName"/>. An empty string therefore means a row
    /// exists and holds an empty value, which the legacy reader could not express.
    /// </returns>
    Task<string?> GetSettingAsync(string settingName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves every host setting as a single materialised, read-only
    /// projection keyed by setting name.
    /// </summary>
    /// <param name="cancellationToken">Signals that the lookup should be abandoned.</param>
    /// <returns>
    /// A task whose completed value is a read-only projection of every setting
    /// name to its stored value. The projection is empty when the table holds no
    /// rows and is never <see langword="null"/>, so callers need no emptiness
    /// guard beyond the usual count check.
    /// </returns>
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default);

    // MIGRATION: The separate add and update primitives collapse into one upsert.
    // Library/Components/Providers/Data/DataProvider.vb L89 AddHostSetting and
    // L90 UpdateHostSetting were two distinct write operations, and
    // Library/Components/Host/HostSettingsController.vb L46-L59 read the row
    // first and branched between them, so the caller-facing legacy contract was
    // already upsert semantics. Exposing the two primitives separately would push
    // that read-then-branch decision back onto every caller, which would be a
    // regression rather than a translation. The isSecure default of false
    // reproduces the legacy two-argument overload at
    // Library/Components/Host/HostSettingsController.vb L42-L44 exactly.

    /// <summary>
    /// Stores a host setting, inserting the row when no setting of that name
    /// exists and overwriting the stored value when one already does.
    /// </summary>
    /// <param name="settingName">Name of the setting to store.</param>
    /// <param name="settingValue">
    /// Value to store against <paramref name="settingName"/>. An empty string is
    /// stored as an empty string and is not treated as a request to remove the
    /// row.
    /// </param>
    /// <param name="isSecure">
    /// Whether the value is sensitive and should be withheld from callers that
    /// are not entitled to it. Defaults to <see langword="false"/>, matching the
    /// legacy two-argument overload.
    /// </param>
    /// <param name="cancellationToken">Signals that the write should be abandoned.</param>
    /// <returns>
    /// A task that completes once the setting has been persisted.
    /// </returns>
    Task UpsertSettingAsync(string settingName, string settingValue, bool isSecure = false, CancellationToken cancellationToken = default);
}
