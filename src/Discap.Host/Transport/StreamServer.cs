using System.Net;
using System.Net.Sockets;

namespace Discap.Host.Transport;

/// <summary>
/// TCP server that listens on localhost for connections from the Android client
/// (routed through ADB port forwarding).
///
/// Accepts one client at a time (single-display streaming).
/// Configures the socket for minimum latency:
/// - TCP_NODELAY: disables Nagle algorithm (no buffering delay)
/// - Large send buffer: handles burst writes for large frames
/// </summary>
public sealed class StreamServer : IDisposable
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;
    private readonly int _port;

    /// <summary>Whether a client is currently connected.</summary>
    public bool IsClientConnected => _client?.Connected ?? false;

    /// <summary>The network stream for the connected client, or null.</summary>
    public NetworkStream? ClientStream => _stream;

    public StreamServer(int port)
    {
        _port = port;
    }

    /// <summary>
    /// Starts listening for connections on localhost:{port}.
    /// </summary>
    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start(1); // Backlog of 1 — we only support one client.
        Console.WriteLine($"[NET] Listening on 127.0.0.1:{_port}");
    }

    /// <summary>
    /// Waits for a client to connect. Blocks until a connection is established.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the wait.</param>
    /// <returns>True if a client connected successfully.</returns>
    public async Task<bool> WaitForClientAsync(CancellationToken cancellationToken = default)
    {
        if (_listener == null)
        {
            Console.Error.WriteLine("[NET] Server not started");
            return false;
        }

        // Disconnect any existing client.
        DisconnectClient();

        Console.WriteLine("[NET] Waiting for Android client to connect...");

        try
        {
            _client = await _listener.AcceptTcpClientAsync(cancellationToken);

            // Configure socket for minimum latency.
            _client.NoDelay = true;                     // TCP_NODELAY — no Nagle buffering
            _client.SendBufferSize = 2 * 1024 * 1024;   // 2MB send buffer for large frames
            _client.ReceiveBufferSize = 64 * 1024;       // 64KB receive buffer for input events
            _client.SendTimeout = 5000;                  // 5s timeout to detect dead connections
            _client.LingerState = new LingerOption(true, 0); // Immediate close on dispose

            _stream = _client.GetStream();

            var endpoint = _client.Client.RemoteEndPoint as IPEndPoint;
            Console.WriteLine($"[NET] Client connected from {endpoint?.Address}:{endpoint?.Port}");

            return true;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[NET] Connection wait cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NET] Accept error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes data to the connected client.
    /// Returns false if the write fails (client disconnected).
    /// </summary>
    public bool Write(byte[] buffer, int offset, int count)
    {
        if (_stream == null || _client == null || !_client.Connected)
            return false;

        try
        {
            _stream.Write(buffer, offset, count);
            return true;
        }
        catch (Exception)
        {
            // Client disconnected or write failed.
            return false;
        }
    }

    /// <summary>
    /// Disconnects the current client.
    /// </summary>
    public void DisconnectClient()
    {
        if (_stream != null)
        {
            try { _stream.Dispose(); } catch { }
            _stream = null;
        }

        if (_client != null)
        {
            try { _client.Dispose(); } catch { }
            _client = null;
            Console.WriteLine("[NET] Client disconnected");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisconnectClient();

        if (_listener != null)
        {
            try { _listener.Stop(); } catch { }
            _listener = null;
            Console.WriteLine("[NET] Server stopped");
        }
    }
}
