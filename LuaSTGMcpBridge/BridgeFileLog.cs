using System;
using System.IO;
using System.Text;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace LuaSTGMcpBridge
{
    /// <summary>
    /// Bridge-private file logger. It deliberately writes to <c>mcp-bridge.log</c> and never to the
    /// editor's own <c>editor.log</c>.
    /// </summary>
    /// <remarks>
    /// The editor keeps its own Serilog 4.2 logger for <c>editor.log</c>. The bridge loads its own
    /// Serilog 4.3 and keeps a <b>private</b> logger instance; it never assigns
    /// <see cref="Log.Logger"/>, so the host's editor logger is left untouched. The file sink is
    /// intentionally tiny so the bridge does not need an extra Serilog.Sinks.File assembly (the
    /// host copy targets Serilog 4.2 and would bind to the wrong Serilog identity).
    /// </remarks>
    internal static class BridgeFileLog
    {
        private static readonly object Gate = new object();
        private static bool configured;
        private static string logPath;
        private static ILogger bridgeLogger;

        internal static string LogPath
        {
            get
            {
                lock (Gate)
                {
                    return logPath;
                }
            }
        }

        internal static void Configure()
        {
            if (configured) return;

            lock (Gate)
            {
                if (configured) return;
                configured = true;

                try
                {
                    logPath = ResolveLogPath();
                    bridgeLogger = new LoggerConfiguration()
                        .MinimumLevel.Debug()
                        .WriteTo.Sink(new FileEventSink(logPath))
                        .CreateLogger();
                }
                catch (Exception)
                {
                    // A logging failure must not stop the bridge from starting. Never fall back to
                    // Log.Logger: on v0.78.4 that is the host's editor.log logger.
                    try
                    {
                        bridgeLogger = new LoggerConfiguration().CreateLogger();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        internal static ILogger CreateLogger(string tag)
        {
            Configure();

            ILogger logger = bridgeLogger;
            if (logger == null)
            {
                try
                {
                    logger = new LoggerConfiguration().CreateLogger();
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return logger.ForContext("Tag", tag);
        }

        private static string ResolveLogPath()
        {
            try
            {
                string editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp-bridge.log");
                using (new FileStream(editorPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                }
                return editorPath;
            }
            catch (Exception)
            {
            }

            try
            {
                string directory = Path.Combine(Path.GetTempPath(), "LuaSTG Editor");
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "mcp-bridge.log");
            }
            catch (Exception)
            {
            }

            return Path.Combine(Path.GetTempPath(), "mcp-bridge.log");
        }

        private sealed class FileEventSink : ILogEventSink
        {
            private readonly object gate = new object();
            private readonly string path;

            internal FileEventSink(string path)
            {
                this.path = path;
            }

            public void Emit(LogEvent logEvent)
            {
                if (logEvent == null || string.IsNullOrEmpty(path)) return;

                try
                {
                    string tag = "McpBridge";
                    LogEventPropertyValue tagValue;
                    if (logEvent.Properties.TryGetValue("Tag", out tagValue) && tagValue != null)
                    {
                        tag = tagValue.ToString().Trim('"');
                    }

                    string message = logEvent.MessageTemplate.Render(logEvent.Properties);
                    string line = string.Format(
                        "{0:dd-MM-yyyy HH:mm:ss} [{1}] [{2}] {3}{4}",
                        logEvent.Timestamp.LocalDateTime,
                        logEvent.Level.ToString().ToUpperInvariant(),
                        tag,
                        message,
                        logEvent.Exception == null ? "" : Environment.NewLine + logEvent.Exception);

                    lock (gate)
                    {
                        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch (Exception)
                {
                    // Logging is diagnostic only.
                }
            }
        }
    }
}
