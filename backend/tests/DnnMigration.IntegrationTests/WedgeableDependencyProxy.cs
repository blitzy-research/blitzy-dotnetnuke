using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace DnnMigration.IntegrationTests;

/// <summary>
/// A loopback TCP relay that stands between a host under test and the real SQL Server, and that can be
/// WEDGED on demand: connections stay open and bytes stop being delivered, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// THE FAILURE THIS EXISTS TO REPRODUCE CANNOT BE REPRODUCED BY TAKING THE SERVER AWAY. Pointing a host at
/// an address nothing listens on proves only that a connection attempt is refused, which is the COLD case -
/// the connection pool is empty, so every attempt reaches the network and fails visibly. The dangerous case
/// is the warm one: an instance that has been serving traffic holds pooled connections, and a pooled
/// <c>Open</c> completes without contacting the server at all. A probe that only opens therefore reports the
/// dependency healthy while the dependency is gone, and no test built on an unreachable address can tell.
/// </para>
/// <para>
/// Wedging rather than closing is equally deliberate. If the relay closed the sockets, the provider would
/// discover the break immediately and the probe would fail for the wrong reason - a detected disconnection
/// rather than an unanswered request. Holding the bytes reproduces the state that actually misleads: the
/// socket is alive, the route is up, and nothing sent is ever answered.
/// </para>
/// <para>
/// Loopback only, on an ephemeral port, for the lifetime of one test.
/// </para>
/// </remarks>
internal sealed class WedgeableDependencyProxy : IAsyncDisposable
{
    /// <summary>How long a relay pump waits for bytes before re-reading the wedge flag.</summary>
    private static readonly TimeSpan PumpPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Relay buffer size, sized for TDS packets rather than for throughput.</summary>
    private const int RelayBufferSize = 16 * 1024;

    /// <summary>The upstream server this relay forwards to.</summary>
    private readonly string _upstreamHost;

    /// <summary>The upstream port this relay forwards to.</summary>
    private readonly int _upstreamPort;

    /// <summary>Accepts the host's connections.</summary>
    private readonly TcpListener _listener;

    /// <summary>Ends the accept loop and every pump when the relay is disposed.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>The accept loop, retained so disposal can wait for it.</summary>
    private readonly Task _acceptLoop;

    /// <summary>Every socket this relay has accepted or opened, so disposal can close all of them.</summary>
    private readonly List<Socket> _sockets = [];

    /// <summary>Guards <see cref="_sockets"/>, which the accept loop and disposal both touch.</summary>
    private readonly object _socketsGate = new();

    /// <summary>Whether the dependency is currently wedged. Volatile: written by a test, read by pumps.</summary>
    private volatile bool _wedged;

    /// <summary>Initialises a new instance of the <see cref="WedgeableDependencyProxy"/> class.</summary>
    /// <param name="upstreamHost">Host name or address of the real server.</param>
    /// <param name="upstreamPort">Port of the real server.</param>
    private WedgeableDependencyProxy(string upstreamHost, int upstreamPort)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    /// <summary>The loopback port a host under test should be pointed at.</summary>
    public int Port { get; }

    /// <summary>
    /// Starts a relay in front of the server named by an existing connection string, and returns both the
    /// relay and a connection string addressed to it.
    /// </summary>
    /// <param name="upstreamConnectionString">A working connection string for the real server.</param>
    /// <returns>The relay and the connection string that reaches the server through it.</returns>
    /// <exception cref="ArgumentException">The connection string names no data source.</exception>
    public static (WedgeableDependencyProxy Proxy, string ConnectionString) InFrontOf(
        string upstreamConnectionString)
    {
        SqlConnectionStringBuilder upstream = new(upstreamConnectionString);
        (string host, int port) = SplitDataSource(upstream.DataSource);

        WedgeableDependencyProxy proxy = new(host, port);

        SqlConnectionStringBuilder relayed = new(upstreamConnectionString)
        {
            DataSource = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1,{proxy.Port}"),

            // The relay is transparent at the byte level, so encryption still works through it, but the
            // certificate names the real server rather than the loopback address the client now addresses.
            TrustServerCertificate = true,

            // Bounded so a wedged relay cannot leave a connection attempt outstanding for the provider's
            // fifteen-second default, which would outlive the probe this suite is measuring.
            ConnectTimeout = 5,
        };

        return (proxy, relayed.ConnectionString);
    }

    /// <summary>
    /// Wedges the dependency: sockets stay open and nothing sent over them is delivered in either direction.
    /// </summary>
    public void Wedge() => _wedged = true;

    /// <summary>Restores delivery, so a recovery can be observed without restarting the host.</summary>
    public void Restore() => _wedged = false;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        _listener.Stop();

        lock (_socketsGate)
        {
            foreach (Socket socket in _sockets)
            {
                try
                {
                    socket.Close();
                }
                catch (SocketException)
                {
                    // Already gone. Disposal has nothing to report and nothing to retry.
                }
                catch (ObjectDisposedException)
                {
                    // Closed by its own pump first, which is the ordinary case.
                }
            }

            _sockets.Clear();
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The accept loop ends by cancellation, so this is how it ends normally.
        }

        _lifetime.Dispose();
    }

    /// <summary>Splits a <c>host,port</c> data source, defaulting to the SQL Server port when absent.</summary>
    /// <param name="dataSource">The data source to split.</param>
    /// <returns>The host and port.</returns>
    /// <exception cref="ArgumentException">The data source is empty.</exception>
    private static (string Host, int Port) SplitDataSource(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new ArgumentException("The connection string names no data source.", nameof(dataSource));
        }

        string[] parts = dataSource.Split(',', 2, StringSplitOptions.TrimEntries);

        return parts.Length == 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int port)
            ? (parts[0], port)
            : (parts[0], 1433);
    }

    /// <summary>Accepts client connections and pairs each with its own upstream connection.</summary>
    /// <param name="cancellationToken">Ends the loop when the relay is disposed.</param>
    /// <returns>A task that completes when the relay stops accepting.</returns>
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;

            try
            {
                client = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Track(client);

            Socket upstream = new(SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The upstream refused this pairing. The client's own attempt fails, which is a faithful
                // relay of what happened rather than something this class should absorb.
                client.Close();
                upstream.Dispose();
                continue;
            }

            Track(upstream);

            _ = Task.Run(() => PumpAsync(client, upstream, cancellationToken), CancellationToken.None);
            _ = Task.Run(() => PumpAsync(upstream, client, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>Records a socket so disposal closes it even if its pump never runs.</summary>
    /// <param name="socket">The socket to record.</param>
    private void Track(Socket socket)
    {
        lock (_socketsGate)
        {
            _sockets.Add(socket);
        }
    }

    /// <summary>
    /// Forwards bytes from one socket to the other, holding every byte for as long as the relay is wedged.
    /// </summary>
    /// <param name="from">The socket read from.</param>
    /// <param name="to">The socket written to.</param>
    /// <param name="cancellationToken">Ends the pump when the relay is disposed.</param>
    /// <returns>A task that completes when either side closes or the relay is disposed.</returns>
    private async Task PumpAsync(Socket from, Socket to, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[RelayBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await from.ReceiveAsync(buffer, SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return;
                }

                // THE BLACK HOLE. The bytes have been taken off the sender's socket and are simply not
                // delivered, so the peer waits for an answer that never comes, on a connection that never
                // closes. Held rather than discarded, so a restored relay resumes a valid TDS stream.
                while (_wedged && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(PumpPollInterval, cancellationToken).ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
        catch (SocketException)
        {
            // Either side went away. Relaying is over and there is nothing to report.
        }
        catch (ObjectDisposedException)
        {
            // Closed by disposal while this pump was in flight.
        }
        finally
        {
            Close(from);
            Close(to);
        }
    }

    /// <summary>Closes a socket, tolerating every way it may already be closed.</summary>
    /// <param name="socket">The socket to close.</param>
    private static void Close(Socket socket)
    {
        try
        {
            socket.Close();
        }
        catch (SocketException)
        {
            // Already gone.
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }
    }
}
