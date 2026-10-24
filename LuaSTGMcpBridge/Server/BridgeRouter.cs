using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using LuaSTGMcpBridge.Protocol;
using Newtonsoft.Json.Linq;

namespace LuaSTGMcpBridge.Server
{
    /// <summary>
    /// Maps a request <c>action</c> onto a handler and turns the result into a response.
    /// </summary>
    /// <remarks>
    /// Handlers run on the TCP thread, never on the UI thread, so a handler that touches the
    /// editor must marshal through <see cref="Threading.UiThread"/>. Registering is thread safe
    /// and additive, which keeps the protocol open for the later MCP commands.
    /// </remarks>
    public sealed class BridgeRouter
    {
        private readonly ConcurrentDictionary<string, Func<JObject, JObject>> handlers =
            new ConcurrentDictionary<string, Func<JObject, JObject>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the registered action names.
        /// </summary>
        public IEnumerable<string> Actions
        {
            get { return handlers.Keys; }
        }

        /// <summary>
        /// Registers (or replaces) the handler of an action.
        /// </summary>
        /// <param name="action">Action name, matched case insensitively.</param>
        /// <param name="handler">Receives the request and returns the response.</param>
        public void Register(string action, Func<JObject, JObject> handler)
        {
            if (string.IsNullOrWhiteSpace(action))
                throw new ArgumentException("Action name must not be empty.", nameof(action));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            handlers[action.Trim()] = handler;
        }

        /// <summary>
        /// Removes the handler of an action.
        /// </summary>
        /// <param name="action">Action name.</param>
        /// <returns><c>true</c> when a handler was removed.</returns>
        public bool Unregister(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return false;
            return handlers.TryRemove(action.Trim(), out _);
        }

        /// <summary>
        /// Routes one request to its handler.
        /// </summary>
        /// <param name="request">The parsed request.</param>
        /// <returns>The response object, never <c>null</c>.</returns>
        public JObject Dispatch(JObject request)
        {
            string action = BridgeProtocol.ReadAction(request);

            if (string.IsNullOrEmpty(action))
                return BridgeProtocol.Failure("request is missing a string 'action' field", "bad_request");

            Func<JObject, JObject> handler;
            if (!handlers.TryGetValue(action, out handler))
                return BridgeProtocol.Failure("unknown action '" + action + "'", "unknown_action");

            return handler(request) ?? BridgeProtocol.Failure("handler of '" + action + "' returned no response");
        }
    }
}
