using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
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

        // MUST stay first: static field initializers run in declaration order, so this installs
        // the BridgeDeps resolver before Logger/RouterInstance touch Serilog/Newtonsoft.Json.
        // Registering the resolver from a static constructor would be too late.
        private static readonly bool DependencySandboxInstalled = BridgeBootstrap.EnsureInstalled();
        private static readonly object SyncRoot = new object();
        private static readonly ILogger Logger = BridgeFileLog.CreateLogger("McpBridgeHost");
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
            if (!DependencySandboxInstalled)
            {
                Logger.Warning("BridgeDeps resolver was not installed; dependency hijacking is disabled.");
            }

            Logger.Information("MCP bridge log: {LogPath}", BridgeFileLog.LogPath);
            Logger.Information("MCP bridge starting for editor {EditorVersion}; compatible={Compatible}.",
                EditorCompat.HostVersion, EditorCompat.IsCompatible);

            // Which build of the shared libraries actually got bound. A binding redirect in
            // LuaSTGEditorSharp.exe.config makes the host's lib\Newtonsoft.Json.dll win even when it
            // is an older build of the same 13.0.0.0 version, and BridgeBootstrap cannot intervene.
            Logger.Information("Bound libraries: {JsonAssembly}; {SerilogAssembly}.",
                DescribeAssembly(typeof(JToken).Assembly), DescribeAssembly(typeof(Log).Assembly));

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

        /// <summary>
        /// Describes a bound assembly, including its file version, so the log shows which build of a
        /// shared library actually won the binding.
        /// </summary>
        /// <param name="assembly">Assembly to describe.</param>
        /// <returns>A one line description.</returns>
        private static string DescribeAssembly(Assembly assembly)
        {
            if (assembly == null) return "unknown";

            try
            {
                AssemblyName name = assembly.GetName();
                string description = name.Name + " " + name.Version;

                try
                {
                    string location = assembly.Location;
                    if (!string.IsNullOrEmpty(location) && File.Exists(location))
                    {
                        string fileVersion = FileVersionInfo.GetVersionInfo(location).FileVersion;
                        if (!string.IsNullOrEmpty(fileVersion)) description += " (file " + fileVersion + ")";
                        description += " @ " + location;
                    }
                }
                catch (Exception)
                {
                }

                return description;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static BridgeRouter CreateRouter()
        {
            BridgeRouter router = new BridgeRouter();

            // The handshake required by the v0.6.0 protocol.
            // "engine" and "plugin" are read from the loaded plugin at request time, so the same
            // bridge binary reports "LuaSTG Sub", "LuaSTG ExPlus", ... correctly on any engine.
            router.Register("ping", request => new JObject
            {
                ["status"] = "pong",
                ["editor"] = "connected",
                ["editorVersion"] = EditorCompat.HostVersion,
                ["engine"] = EditorCompat.EngineName,
                ["plugin"] = EditorCompat.PluginName,
                ["bridgeCompatible"] = EditorCompat.IsCompatible
            });

            // Demonstrates the UI-thread marshalling: reading the active document touches WPF.
            router.Register("editor_state", request =>
            {
                string[] missing = EditorCompat.GetMissing("editor_state");
                return missing.Length == 0
                    ? EditorContext.DescribeActiveDocument()
                    : EditorCompat.Unsupported("editor_state", missing);
            });

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
    /// failure to <c>mcp-bridge.log</c>.
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
                BridgeFileLog.CreateLogger("McpBridgePlugin")
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
