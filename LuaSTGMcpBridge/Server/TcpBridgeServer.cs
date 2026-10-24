using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LuaSTGMcpBridge.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace LuaSTGMcpBridge.Server
{
    /// <summary>
    /// Line delimited JSON server bound to the IPv4 loopback address (127.0.0.1 only).
    /// </summary>
    /// <remarks>
    /// One request per line, one response line back. Every client connection is served by its
    /// own task; handlers are invoked on those tasks, so they must not touch WPF objects
    /// directly (see <see cref="Threading.UiThread"/>).
    /// <para/>
    /// Binding to <see cref="IPAddress.Loopback"/> keeps the bridge unreachable from the
    /// network: only processes on this machine can connect.
    /// </remarks>
    public sealed class TcpBridgeServer : IDisposable
    {
        private const int BufferSize = 4096;

        private const string InternalErrorLiteral = "{\"status\":\"error\",\"error\":\"internal bridge error\"}";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly object syncRoot = new object();
        private readonly BridgeRouter router;
        private readonly int port;
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();

        private ILogger logger = BridgeFileLog.CreateLogger("McpBridgeServer");
        private TcpListener listener;
        private CancellationTokenSource cancellation;
        private Task acceptLoop;
        private bool running;
        private bool disposed;

        /// <summary>
        /// Initialises the server. The port is not bound until <see cref="Start"/> is called.
        /// </summary>
        /// <param name="port">TCP port on 127.0.0.1.</param>
        /// <param name="router">Router that handles the requests.</param>
        public TcpBridgeServer(int port, BridgeRouter router)
        {
            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
            if (router == null)
                throw new ArgumentNullException(nameof(router));

            this.port = port;
            this.router = router;
        }

        /// <summary>
        /// Gets the port this server binds to.
        /// </summary>
        public int Port
        {
            get { return port; }
        }

        /// <summary>
        /// Gets whether the listener is currently accepting connections.
        /// </summary>
        public bool IsRunning
        {
            get { lock (syncRoot) { return running; } }
        }

        /// <summary>
        /// Gets or sets the logger used for diagnostics. Setting <c>null</c> restores the default.
        /// </summary>
        public ILogger Logger
        {
            get { return logger; }
            set { logger = value ?? BridgeFileLog.CreateLogger("McpBridgeServer"); }
        }

        /// <summary>
        /// Binds 127.0.0.1:<see cref="Port"/> and starts accepting clients.
        /// </summary>
        /// <exception cref="SocketException">The port is already in use.</exception>
        public void Start()
        {
            lock (syncRoot)
            {
                if (disposed) throw new ObjectDisposedException(GetType().Name);
                if (running) return;

                TcpListener created = new TcpListener(IPAddress.Loopback, port);
                created.Start();

                listener = created;
                cancellation = new CancellationTokenSource();
                running = true;

                CancellationToken token = cancellation.Token;
                acceptLoop = Task.Factory.StartNew(
                    () => AcceptLoop(created, token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            logger.Information("MCP bridge listening on 127.0.0.1:{Port}.", port);
        }

        /// <summary>
        /// Stops accepting, closes every live client and waits briefly for the accept loop.
        /// Safe to call when already stopped.
        /// </summary>
        public void Stop()
        {
            CancellationTokenSource cancellationToCancel;
            TcpListener listenerToStop;
            TcpClient[] clientsToClose;
            Task loop;

            lock (syncRoot)
            {
                if (!running) return;

                running = false;
                cancellationToCancel = cancellation;
                cancellation = null;
                listenerToStop = listener;
                listener = null;
                loop = acceptLoop;
                acceptLoop = null;

                clientsToClose = new TcpClient[clients.Count];
                clients.CopyTo(clientsToClose);
                clients.Clear();
            }

            try { cancellationToCancel?.Cancel(); }
            catch (ObjectDisposedException) { }

            try { listenerToStop?.Stop(); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }

            foreach (TcpClient client in clientsToClose)
            {
                try { client.Close(); }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            }

            if (loop != null)
            {
                try { loop.Wait(TimeSpan.FromSeconds(2)); }
                catch (AggregateException) { }
            }

            cancellationToCancel?.Dispose();
            logger.Information("MCP bridge listener on 127.0.0.1:{Port} stopped.", port);
        }

        /// <summary>
        /// Stops the server and releases resources.
        /// </summary>
        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed) return;
            }

            Stop();

            lock (syncRoot)
            {
                disposed = true;
            }
        }

        private async Task AcceptLoop(TcpListener activeListener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await activeListener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break; // Stop() disposed the listener.
                }
                catch (SocketException ex)
                {
                    if (token.IsCancellationRequested) break;

                    logger.Error("MCP bridge accept failed: {Message}", ex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;

                    logger.Error("MCP bridge accept loop stopped: {Message}", ex.Message);
                    break;
                }

                HandleClient(client, token);
            }
        }

        private void HandleClient(TcpClient client, CancellationToken token)
        {
            lock (syncRoot)
            {
                if (!running)
                {
                    // Stopped between accept and registration.
                    try { client.Close(); }
                    catch (ObjectDisposedException) { }
                    return;
                }

                clients.Add(client);
            }

            Task.Run(() => ServeClientAsync(client, token));
        }

        private async Task ServeClientAsync(TcpClient client, CancellationToken token)
        {
            EndPoint remote = null;

            try
            {
                client.NoDelay = true;
                remote = client.Client.RemoteEndPoint;
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }

            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Utf8NoBom, false, BufferSize, true))
                using (StreamWriter writer = new StreamWriter(stream, Utf8NoBom, BufferSize, true))
                {
                    writer.NewLine = BridgeProtocol.LineTerminator;
                    writer.AutoFlush = true;

                    while (!token.IsCancellationRequested)
                    {
                        string line;

                        try
                        {
                            line = await reader.ReadLineAsync().ConfigureAwait(false);
                        }
                        catch (IOException) { break; }
                        catch (ObjectDisposedException) { break; }

                        if (line == null) break; // Client closed its half of the socket.
                        if (line.Length == 0) continue;

                        string response = ProcessLine(line);
                        await writer.WriteLineAsync(response).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                logger.Error("MCP bridge client {Remote} failed: {Message}", remote, ex.Message);
            }
            finally
            {
                lock (syncRoot) { clients.Remove(client); }
            }
        }

        private string ProcessLine(string line)
        {
            try
            {
                JObject request;
                string error;

                if (!BridgeProtocol.TryParse(line, out request, out error))
                    return SafeError(error, "bad_request");

                return EncodeOrLiteral(router.Dispatch(request));
            }
            catch (Exception ex)
            {
                logger.Error("MCP bridge request failed: {Message}", ex.Message);
                return SafeError(ex.Message, "error");
            }
        }

        /// <summary>
        /// Encodes a response, falling back to a hand written JSON literal.
        /// </summary>
        /// <remarks>
        /// On the v0.78.3 host a <see cref="MissingMethodException"/> thrown inside
        /// <see cref="BridgeProtocol.Encode"/> escaped the catch block below, because the error
        /// path serialised its own response with the same broken call. The client then saw a
        /// dropped connection instead of an error message. This literal depends on no library.
        /// </remarks>
        private static string EncodeOrLiteral(JObject payload)
        {
            try
            {
                return BridgeProtocol.Encode(payload);
            }
            catch (Exception)
            {
                return InternalErrorLiteral;
            }
        }

        private static string SafeError(string message, string code)
        {
            try
            {
                return BridgeProtocol.Encode(BridgeProtocol.Failure(message, code));
            }
            catch (Exception)
            {
                return InternalErrorLiteral;
            }
        }
    }
}
