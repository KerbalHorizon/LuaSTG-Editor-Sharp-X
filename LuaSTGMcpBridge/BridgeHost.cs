using System;
using System.Net.Sockets;
using LuaSTGMcpBridge.Actions;
using LuaSTGMcpBridge.Editor;
using LuaSTGMcpBridge.Protocol;
using LuaSTGMcpBridge.Server;
using Newtonsoft.Json.Linq;
using Serilog;

namespace LuaSTGMcpBridge
{
    /// <summary>
    /// Process wide lifecycle of the bridge. Call <see cref="Start()"/> once the editor is up.
    /// </summary>
    /// <remarks>
    /// The host never throws at its callers: a failed bind (port taken, firewall, ...) is logged
    /// and reported as <c>false</c>, so a broken bridge can never stop the editor from starting.
    /// <para/>
    /// Registered actions:
    /// <list type="bullet">
    /// <item><description><c>ping</c> -&gt; <c>{"status":"pong","editor":"connected"}</c></description></item>
    /// <item><description><c>editor_state</c> -&gt; active document summary (runs on the UI thread)</description></item>
    /// </list>
    /// </remarks>
    public static class BridgeHost
    {
        /// <summary>
        /// Default port, chosen by the v0.6.0 bridge design.
        /// </summary>
        public const int DefaultPort = 8090;

        private static readonly object SyncRoot = new object();
        private static readonly ILogger Logger = Log.ForContext("Tag", "McpBridgeHost");
        private static readonly BridgeRouter RouterInstance = CreateRouter();

        private static TcpBridgeServer server;

        /// <summary>
        /// Gets the router so that later versions can add their own actions.
        /// </summary>
        public static BridgeRouter Router
        {
            get { return RouterInstance; }
        }

        /// <summary>
        /// Gets whether the server is accepting connections.
        /// </summary>
        public static bool IsRunning
        {
            get { lock (SyncRoot) { return server != null && server.IsRunning; } }
        }

        /// <summary>
        /// Gets the port being listened on, or <c>null</c> when stopped.
        /// </summary>
        public static int? ListeningPort
        {
            get
            {
                lock (SyncRoot)
                {
                    return server != null && server.IsRunning ? (int?)server.Port : null;
                }
            }
        }

        /// <summary>
        /// Starts the bridge on <see cref="DefaultPort"/>.
        /// </summary>
        /// <returns><c>true</c> when the bridge is listening.</returns>
        public static bool Start()
        {
            return Start(DefaultPort);
        }

        /// <summary>
        /// Starts the bridge on <paramref name="port"/>, restarting it when another port is active.
        /// </summary>
        /// <param name="port">TCP port on 127.0.0.1.</param>
        /// <returns><c>true</c> when the bridge is listening.</returns>
        public static bool Start(int port)
        {
            lock (SyncRoot)
            {
                if (server != null && server.IsRunning)
                {
                    if (server.Port == port) return true;
                    StopCore();
                }

                try
                {
                    TcpBridgeServer created = new TcpBridgeServer(port, RouterInstance);
                    created.Start();
                    server = created;

                    Logger.Information("MCP bridge ready on 127.0.0.1:{Port}.", port);
                    return true;
                }
                catch (SocketException ex)
                {
                    Logger.Error("MCP bridge cannot bind 127.0.0.1:{Port}: {Message}", port, ex.Message);
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Error("MCP bridge failed to start: {Message}", ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Stops the bridge. Safe to call when already stopped or during shutdown.
        /// </summary>
        public static void Stop()
        {
            lock (SyncRoot) StopCore();
        }

        private static void StopCore()
        {
            TcpBridgeServer toStop = server;
            server = null;

            if (toStop == null) return;

            try
            {
                toStop.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warning("MCP bridge stop failed: {Message}", ex.Message);
            }
            finally
            {
                toStop.Dispose();
            }
        }

        private static BridgeRouter CreateRouter()
        {
            BridgeRouter router = new BridgeRouter();

            // The handshake required by the v0.6.0 protocol.
            router.Register("ping", request => new JObject
            {
                ["status"] = "pong",
                ["editor"] = "connected"
            });

            // Demonstrates the UI-thread marshalling: reading the active document touches WPF.
            router.Register("editor_state", request => EditorContext.DescribeActiveDocument());

            // v0.6.0 tree commands. Every handler marshals to the UI thread and mutates the tree
            // exclusively through EditorData commands, so Undo / Redo keeps working.
            router.Register("get_tree", request => TreeActions.GetTree(request));
            router.Register("set_attribute", request => TreeActions.SetAttribute(request));
            router.Register("insert_node", request => TreeActions.InsertNode(request));
            router.Register("delete_node", request => TreeActions.DeleteNode(request));

            return router;
        }
    }

    /// <summary>
    /// Logging entry point for failures that happen before, or while, <see cref="BridgeHost"/>
    /// is being initialized.
    /// </summary>
    /// <remarks>
    /// This type deliberately has no static fields: if the <see cref="BridgeHost"/> type
    /// initializer fails (for example because Serilog is not the version the bridge was built
    /// against), calling any member of <c>BridgeHost</c> again would just rethrow
    /// <see cref="TypeInitializationException"/>. A separate, field-free type can still write the
    /// failure to the editor log.
    /// </remarks>
    public static class BridgeLog
    {
        /// <summary>
        /// Records a bridge startup failure without ever throwing at the caller.
        /// </summary>
        /// <param name="exception">The failure to record.</param>
        public static void StartupFailure(Exception exception)
        {
            try
            {
                Log.ForContext("Tag", "McpBridgePlugin")
                    .Error(exception, "The MCP bridge could not be started.");
            }
            catch (Exception)
            {
                // Logging must never turn a handled failure into an editor crash.
            }

            try
            {
                Console.Error.WriteLine(
                    "[LuaSTGMcpBridge] The MCP bridge could not be started: " + exception);
            }
            catch (Exception)
            {
            }
        }
    }
}
