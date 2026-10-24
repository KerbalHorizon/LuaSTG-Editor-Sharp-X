using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LuaSTGMcpBridge.Protocol
{
    /// <summary>
    /// JSON helpers that only touch API surface present in <b>every</b> Newtonsoft.Json 13.0.x build.
    /// </summary>
    /// <remarks>
    /// Why this exists (real production failure, v0.78.3 pure Sub host):
    /// <para/>
    /// The editor binds Newtonsoft.Json through a <c>0.0.0.0-13.0.0.0</c> binding redirect in
    /// <c>LuaSTGEditorSharp.exe.config</c> to its own <c>lib\Newtonsoft.Json.dll</c>, which also
    /// stamps assembly version <c>13.0.0.0</c>. A redirect makes the CLR resolve the reference
    /// <i>successfully</i>, so <c>AppDomain.AssemblyResolve</c> - and therefore the
    /// <c>BridgeDeps</c> resolver in <see cref="BridgeBootstrap"/> - is never invoked. The host's
    /// build always wins, even though it is a different build of the same version number.
    /// <para/>
    /// 13.0.4 added the <c>JToken.ToString(Formatting)</c> convenience overload without bumping the
    /// assembly version. C# binds that exact one-parameter overload when both exist, so the call
    /// threw <see cref="MissingMethodException"/> on the host build, which only has
    /// <c>ToString(Formatting, params JsonConverter[])</c>.
    /// <para/>
    /// Everything here is expressed with <c>JToken.WriteTo(JsonWriter)</c> and
    /// <c>JToken.Parse(string)</c>, which have existed for a decade. <b>Do not reintroduce an
    /// explicit <c>Formatting</c> argument</b> - the parameterless <c>ToString()</c> is indented,
    /// which would break the one-message-per-line protocol.
    /// </remarks>
    internal static class JsonCompat
    {
        /// <summary>
        /// Explicit empty converter array.
        /// </summary>
        /// <remarks>
        /// This exists to defeat overload resolution, not to convert anything. 13.0.4 added
        /// one-parameter convenience overloads next to the original <c>params</c> methods
        /// (<c>WriteTo(JsonWriter)</c>, <c>ToString(Formatting)</c>). Calling
        /// <c>token.WriteTo(writer)</c> binds that new overload at compile time, and the host build
        /// then throws <see cref="MissingMethodException"/>. Passing the array explicitly forces the
        /// <c>WriteTo(JsonWriter, JsonConverter[])</c> signature that every version has.
        /// </remarks>
        private static readonly JsonConverter[] NoConverters = new JsonConverter[0];

        /// <summary>
        /// Serialises a token without any indentation and without depending on newer overloads.
        /// </summary>
        /// <param name="token">The token to serialise.</param>
        /// <returns>Compact JSON on a single line.</returns>
        public static string ToCompactString(JToken token)
        {
            if (token == null) return "null";

            using (StringWriter text = new StringWriter(CultureInfo.InvariantCulture))
            using (JsonTextWriter writer = new JsonTextWriter(text))
            {
                writer.Formatting = Formatting.None;
                token.WriteTo(writer, NoConverters);
                writer.Flush();
                return text.ToString();
            }
        }

        /// <summary>
        /// Parses one request line into a JSON object.
        /// </summary>
        /// <param name="raw">The raw line received from the socket.</param>
        /// <param name="request">The parsed object, or <c>null</c> on failure.</param>
        /// <param name="error">A human readable reason when parsing fails.</param>
        /// <returns><c>true</c> when <paramref name="raw"/> is a JSON object.</returns>
        public static bool TryParseObject(string raw, out JObject request, out string error)
        {
            request = null;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "empty request";
                return false;
            }

            try
            {
                JToken token = JToken.Parse(raw);
                JObject obj = token as JObject;

                if (obj == null)
                {
                    error = "request must be a JSON object";
                    return false;
                }

                request = obj;
                return true;
            }
            catch (JsonException ex)
            {
                error = "invalid JSON: " + ex.Message;
                return false;
            }
        }
    }
}
