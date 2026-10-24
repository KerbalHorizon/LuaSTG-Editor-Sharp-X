using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace LuaSTGMcpBridge.Protocol
{
    /// <summary>
    /// Wire format of the bridge: one compact JSON object per line (newline delimited JSON).
    /// </summary>
    /// <remarks>
    /// A request always carries a string <c>action</c> field, e.g. <c>{"action":"ping"}</c>.
    /// A response always carries a string <c>status</c> field. Errors use
    /// <c>{"status":"error","error":"..."}</c>.
    /// </remarks>
    public static class BridgeProtocol
    {
        /// <summary>
        /// Terminator used between messages. The client must send it as well.
        /// </summary>
        public const string LineTerminator = "\n";

        /// <summary>
        /// Encodes a response for the wire. A single line, no indentation.
        /// </summary>
        /// <param name="payload">The response object.</param>
        /// <returns>Compact JSON.</returns>
        public static string Encode(JObject payload)
        {
            return payload == null ? "{}" : JsonCompat.ToCompactString(payload);
        }

        /// <summary>
        /// Parses one request line.
        /// </summary>
        /// <param name="raw">The raw line received from the socket.</param>
        /// <param name="request">The parsed request, or <c>null</c> on failure.</param>
        /// <param name="error">A human readable reason when parsing fails.</param>
        /// <returns><c>true</c> when <paramref name="raw"/> is a JSON object.</returns>
        public static bool TryParse(string raw, out JObject request, out string error)
        {
            return JsonCompat.TryParseObject(raw, out request, out error);
        }

        /// <summary>
        /// Reads the <c>action</c> field of a request, trimmed and case preserved.
        /// </summary>
        /// <param name="request">The parsed request.</param>
        /// <returns>The action name, or <c>null</c> when absent / not a string.</returns>
        public static string ReadAction(JObject request)
        {
            JToken token = request == null ? null : request["action"];
            if (token == null || token.Type != JTokenType.String) return null;

            string action = ((string)token)?.Trim();
            return string.IsNullOrEmpty(action) ? null : action;
        }

        /// <summary>
        /// Builds a minimal response carrying only a status.
        /// </summary>
        /// <param name="status">Value of the <c>status</c> field.</param>
        /// <returns>The response object.</returns>
        public static JObject Success(string status)
        {
            return new JObject { ["status"] = status };
        }

        /// <summary>
        /// Builds an error response.
        /// </summary>
        /// <param name="message">Reason shown to the client.</param>
        /// <param name="code">Value of the <c>status</c> field, <c>error</c> by default.</param>
        /// <returns>The response object.</returns>
        public static JObject Failure(string message, string code = "error")
        {
            return Failure(message, code, null);
        }

        /// <summary>
        /// Builds an error response with an extra payload, for example the attributes a node
        /// actually has when the requested one does not exist.
        /// </summary>
        /// <param name="message">Reason shown to the client.</param>
        /// <param name="code">Value of the <c>status</c> field, <c>error</c> by default.</param>
        /// <param name="details">Extra payload placed under <c>details</c>, may be <c>null</c>.</param>
        /// <returns>The response object.</returns>
        public static JObject Failure(string message, string code, JObject details)
        {
            JObject result = new JObject
            {
                ["status"] = string.IsNullOrEmpty(code) ? "error" : code,
                ["error"] = message ?? "unknown error"
            };

            if (details != null) result["details"] = details;
            return result;
        }

        /// <summary>
        /// Reads a string field, accepting numbers and booleans as their text form.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="name">Field name.</param>
        /// <returns>The value, or <c>null</c> when absent or JSON null.</returns>
        public static string ReadString(JObject request, string name)
        {
            JToken token = request == null ? null : request[name];

            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String) return (string)token;

            return token.ToString();
        }

        /// <summary>
        /// Reads an integer field, accepting a numeric string as well.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="name">Field name.</param>
        /// <returns>The value, or <c>null</c> when absent or not a whole number.</returns>
        public static int? ReadInt(JObject request, string name)
        {
            JToken token = request == null ? null : request[name];

            if (token == null || token.Type == JTokenType.Null) return null;

            if (token.Type == JTokenType.Integer)
            {
                try
                {
                    return Convert.ToInt32((long)token, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    return null;
                }
            }

            if (token.Type == JTokenType.Float)
            {
                double value = (double)token;
                double rounded = Math.Round(value);

                if (Math.Abs(value - rounded) > 1e-9) return null;
                if (rounded < int.MinValue || rounded > int.MaxValue) return null;

                return (int)rounded;
            }

            if (token.Type == JTokenType.String)
            {
                int parsed;
                if (int.TryParse(((string)token).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }

            return null;
        }

        /// <summary>
        /// Reads a boolean field, accepting "true" / "false" strings as well.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="name">Field name.</param>
        /// <param name="defaultValue">Value used when the field is absent or unreadable.</param>
        /// <returns>The value.</returns>
        public static bool ReadBool(JObject request, string name, bool defaultValue = false)
        {
            JToken token = request == null ? null : request[name];

            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            if (token.Type == JTokenType.Boolean) return (bool)token;

            if (token.Type == JTokenType.String)
            {
                bool parsed;
                if (bool.TryParse(((string)token).Trim(), out parsed)) return parsed;
            }

            return defaultValue;
        }

        /// <summary>
        /// Reads an object field.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="name">Field name.</param>
        /// <returns>The object, or <c>null</c> when absent or not an object.</returns>
        public static JObject ReadObject(JObject request, string name)
        {
            JToken token = request == null ? null : request[name];
            return token as JObject;
        }
    }
}
