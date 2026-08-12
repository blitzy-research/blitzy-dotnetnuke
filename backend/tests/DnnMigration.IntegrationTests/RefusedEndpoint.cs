using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DnnMigration.IntegrationTests;

/// <summary>
/// A loopback address on which a connection attempt is refused immediately, obtained from the operating
/// system rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS REPLACES, AND WHY IT WAS A REAL RELIABILITY DEFECT. Two suites needed an address nothing
/// listens on - one to hand a genuine transport failure to the real store-failure classifier, the other to
/// build a host whose database cannot be reached - and each spelled a fixed high port and asserted, in
/// effect, that nothing on the machine had bound it. That assertion is not this suite's to make. Several
/// clones of this repository build and test in parallel on one host, each with its own containers and its
/// own tooling, and any of them may bind any port; a collision does not fail loudly either, because a
/// process that ACCEPTS the connection turns "refused immediately" into a handshake against something
/// unrelated, and the suite then reports whatever that something did. The failure would be intermittent,
/// unreproducible and attributed to the wrong subject.
/// </para>
/// <para>
/// HOW A PORT IS OBTAINED HERE. The operating system is asked for one: a listener is bound to port zero on
/// the loopback interface, which makes the kernel choose a free port from the ephemeral range, the chosen
/// port is recorded, and the listener is then stopped. From that moment the address is one nothing is
/// listening on, which is exactly the property both suites need - and it is a property the kernel
/// established rather than one this file hoped for.
/// </para>
/// <para>
/// ⚠ THE RESIDUAL RACE IS REAL, BOUNDED AND STRICTLY SMALLER THAN THE ONE IT REPLACES. Nothing stops the
/// kernel handing the same ephemeral port to another process after the listener is released. That window
/// is short, the ephemeral range is large, and - decisively - the kernel does not reissue a port it is
/// currently holding, whereas a hard-coded high port is equally available to every process on the machine
/// for the whole life of the run. A "reserve it forever" alternative was considered and rejected: keeping
/// the listener OPEN would make the address one that ACCEPTS connections, which is the opposite of what is
/// wanted, and no portable mechanism reserves a port while refusing connections on it.
/// </para>
/// <para>
/// RESOLVED ONCE PER PROCESS. Both suites share one address, because the property they need is identical
/// and because asking the kernel repeatedly would widen the window above for no benefit. The type is
/// static and holds no disposable state: the listener exists only inside <see cref="Resolve"/>.
/// </para>
/// </remarks>
internal static class RefusedEndpoint
{
    /// <summary>The loopback address both suites point at.</summary>
    private const string LoopbackAddress = "127.0.0.1";

    /// <summary>The port, chosen by the operating system once for the whole process.</summary>
    private static readonly int ResolvedPort = Resolve();

    /// <summary>Gets the loopback host name, which is never resolved through DNS.</summary>
    /// <remarks>
    /// Loopback rather than a routable address on purpose: a test must not depend on name resolution or on
    /// reaching anything outside the machine it runs on.
    /// </remarks>
    public static string Host => LoopbackAddress;

    /// <summary>Gets the port nothing is listening on.</summary>
    public static int Port => ResolvedPort;

    /// <summary>
    /// Gets the address in the comma-separated form SQL Server connection strings use for a port.
    /// </summary>
    /// <remarks>
    /// A colon would be read as part of the instance name by the client, so the separator is a comma. This
    /// is also the value a suite asserts is ABSENT from a published failure report, which is why it is
    /// composed once here rather than spelled at each site.
    /// </remarks>
    public static string ServerAddress =>
        string.Create(CultureInfo.InvariantCulture, $"{LoopbackAddress},{ResolvedPort}");

    /// <summary>
    /// Builds a SQL Server connection string pointed at the refused address.
    /// </summary>
    /// <param name="databaseName">The catalogue name to name in the string.</param>
    /// <param name="userId">The login to name. No real credential is ever supplied.</param>
    /// <param name="connectTimeoutSeconds">
    /// How long the client may wait. Kept small because the address refuses immediately, so a longer value
    /// would only slow a failure down without making it stronger.
    /// </param>
    /// <returns>The connection string.</returns>
    /// <remarks>
    /// No password value that could be mistaken for a credential appears here or at any call site: the
    /// address is unreachable, so the login is never presented to anything.
    /// </remarks>
    public static string ConnectionString(
        string databaseName,
        string userId = "probe",
        int connectTimeoutSeconds = 2) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Server={ServerAddress};Database={databaseName};User Id={userId};"
            + $"Password=not-a-real-credential;TrustServerCertificate=True;Encrypt=True;"
            + $"Connect Timeout={connectTimeoutSeconds}");

    /// <summary>
    /// Asks the operating system for a free loopback port and releases it again.
    /// </summary>
    /// <returns>The port the kernel chose, on which nothing is now listening.</returns>
    /// <remarks>
    /// Port zero is the documented request for "any free port". The listener is stopped in a finally block
    /// so that an exception between bind and read cannot leave a socket listening on the very address this
    /// type promises is refusing connections.
    /// </remarks>
    private static int Resolve()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);

        try
        {
            listener.Start();

            IPEndPoint endpoint = (IPEndPoint)listener.LocalEndpoint;

            return endpoint.Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
