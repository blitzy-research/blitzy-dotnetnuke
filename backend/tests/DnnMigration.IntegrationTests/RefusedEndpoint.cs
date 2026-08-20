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
/// effect, that nothing on the machine had bound it. That assertion is not this suite's to make.
/// </para>
/// <para>
/// ⚠ THE RESIDUAL RACE IS REAL, BOUNDED AND STRICTLY SMALLER THAN THE ONE IT REPLACES. Nothing stops the
/// kernel handing the same ephemeral port to another process after the listener is released.
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

    /// <summary>Gets the address in the comma-separated form SQL Server connection strings use for a port.</summary>
    public static string ServerAddress =>
        string.Create(CultureInfo.InvariantCulture, $"{LoopbackAddress},{ResolvedPort}");

    /// <summary>Builds a SQL Server connection string pointed at the refused address.</summary>
    /// <param name="databaseName">The catalogue name to name in the string.</param>
    /// <param name="userId">The login to name.</param>
    /// <param name="connectTimeoutSeconds">How long the client may wait.</param>
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

    /// <summary>Asks the operating system for a free loopback port and releases it again.</summary>
    /// <returns>The port the kernel chose, on which nothing is now listening.</returns>
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
