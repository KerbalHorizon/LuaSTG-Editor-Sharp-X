using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LuaSTGEditorSharp.EditorData;
using LuaSTGEditorSharp.EditorData.Node;
using LuaSTGEditorSharp.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LuaSTGMcpBridge.Editor
{
    /// <summary>
    /// Creates a <see cref="TreeNode"/> by type name and applies initial attribute values.
    /// </summary>
    /// <remarks>
    /// Node types are discovered from <see cref="AbstractNodeTypeCache.NodeTypes"/>, which the host
    /// fills with every <see cref="TreeNode"/> subclass of every loaded assembly. Instances are
    /// built exactly like <see cref="AbstractNodeTypeCache"/> builds its standard nodes: through the
    /// <c>(DocumentData)</c> constructor that every node type provides.
    /// </remarks>
    public static class NodeFactory
    {
        private const string CoreAssemblyName = "LuaSTGEditorSharp.Core";

        /// <summary>
        /// Gets the node types known to the loaded plugin.
        /// </summary>
        /// <returns>The types, empty when no plugin is loaded yet.</returns>
        public static List<Type> GetNodeTypes()
        {
            try
            {
                AbstractNodeTypeCache cache = PluginHandler.Plugin == null ? null : PluginHandler.Plugin.NodeTypeCache;
                if (cache == null || cache.NodeTypes == null) return new List<Type>();

                return cache.NodeTypes;
            }
            catch (Exception)
            {
                return new List<Type>();
            }
        }

        /// <summary>
        /// Creates a node of <paramref name="typeName"/> for <paramref name="document"/> and applies
        /// <paramref name="values"/> to its attributes.
        /// </summary>
        /// <param name="typeName">Simple or full type name, matched case insensitively.</param>
        /// <param name="document">Document the node will belong to.</param>
        /// <param name="values">Attribute values to apply before insertion, may be <c>null</c>.</param>
        /// <param name="node">The created node.</param>
        /// <param name="code">Machine readable failure code.</param>
        /// <param name="message">Human readable failure reason.</param>
        /// <param name="details">Extra failure payload.</param>
        /// <returns><c>true</c> when the node was created and configured.</returns>
        public static bool TryCreate(string typeName, DocumentData document, JObject values,
            out TreeNode node, out string code, out string message, out JObject details)
        {
            node = null;
            code = null;
            message = null;
            details = null;

            if (string.IsNullOrWhiteSpace(typeName))
            {
                code = "bad_request";
                message = "'type' is required, for example \"Comment\"";
                return false;
            }

            Type type;
            if (!TryResolveType(typeName.Trim(), out type, out code, out message, out details)) return false;

            ConstructorInfo constructor = type.GetConstructor(new[] { typeof(DocumentData) });

            if (constructor == null)
            {
                code = "unsupported_node_type";
                message = "node type '" + type.FullName + "' has no (DocumentData) constructor";
                return false;
            }

            TreeNode created;

            try
            {
                created = (TreeNode)constructor.Invoke(new object[] { document });
            }
            catch (Exception ex)
            {
                code = "node_creation_failed";
                message = "node type '" + type.FullName + "' could not be created: " + Unwrap(ex).Message;
                return false;
            }

            if (!TryApplyAttributes(created, values, out code, out message, out details)) return false;

            node = created;
            return true;
        }

        /// <summary>
        /// Applies attribute values to a node.
        /// </summary>
        /// <remarks>
        /// This is only used for a freshly created, not yet attached node: undo removes such a node
        /// as a whole, so no <c>EditAttrCommand</c> is needed and the undo stack stays clean. Values
        /// on nodes that already live in the tree are always changed through <c>EditAttrCommand</c>.
        /// </remarks>
        /// <param name="node">Node to configure.</param>
        /// <param name="values">Attribute name to value map, may be <c>null</c>.</param>
        /// <param name="code">Machine readable failure code.</param>
        /// <param name="message">Human readable failure reason.</param>
        /// <param name="details">Extra failure payload.</param>
        /// <returns><c>true</c> when every value was applied.</returns>
        public static bool TryApplyAttributes(TreeNode node, JObject values,
            out string code, out string message, out JObject details)
        {
            code = null;
            message = null;
            details = null;

            if (node == null || values == null || values.Count == 0) return true;

            foreach (JProperty property in values.Properties())
            {
                AttrItem attribute = FindAttribute(node, property.Name);

                if (attribute == null)
                {
                    code = "unknown_attribute";
                    message = "node type '" + node.GetType().Name + "' has no attribute '" + property.Name + "'";
                    details = new JObject
                    {
                        ["nodeType"] = node.GetType().Name,
                        ["availableAttributes"] = new JArray(GetAttributeNames(node))
                    };
                    return false;
                }

                attribute.AttrInput = ValueToString(property.Value);
            }

            return true;
        }

        /// <summary>
        /// Finds an attribute by its caption.
        /// </summary>
        /// <param name="node">Node that owns the attributes.</param>
        /// <param name="name">Caption to look for.</param>
        /// <returns>The attribute, or <c>null</c> when the node has none with that name.</returns>
        public static AttrItem FindAttribute(TreeNode node, string name)
        {
            if (node == null || string.IsNullOrEmpty(name) || node.Attributes == null) return null;

            foreach (AttrItem attribute in node.Attributes)
            {
                if (attribute == null) continue;
                if (string.Equals(attribute.AttrCap, name, StringComparison.Ordinal)) return attribute;
            }

            foreach (AttrItem attribute in node.Attributes)
            {
                if (attribute == null) continue;
                if (string.Equals(attribute.AttrCap, name, StringComparison.OrdinalIgnoreCase)) return attribute;
            }

            return null;
        }

        /// <summary>
        /// Gets the captions of every attribute of a node.
        /// </summary>
        /// <param name="node">Node to inspect.</param>
        /// <returns>The captions, in declaration order.</returns>
        public static string[] GetAttributeNames(TreeNode node)
        {
            List<string> names = new List<string>();

            if (node == null || node.Attributes == null) return names.ToArray();

            foreach (AttrItem attribute in node.Attributes)
            {
                if (attribute == null || string.IsNullOrEmpty(attribute.AttrCap)) continue;
                names.Add(attribute.AttrCap);
            }

            return names.ToArray();
        }

        private static bool TryResolveType(string typeName, out Type type, out string code, out string message, out JObject details)
        {
            type = null;
            code = null;
            message = null;
            details = null;

            List<Type> available = GetNodeTypes();

            if (available.Count == 0)
            {
                code = "no_plugin";
                message = "no plugin is loaded, so no node types are known yet";
                return false;
            }

            List<Type> matches = available
                .Where(t => string.Equals(t.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                matches = available
                    .Where(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (matches.Count == 0)
            {
                code = "unknown_node_type";
                message = "unknown node type '" + typeName + "'";
                details = new JObject
                {
                    ["suggestions"] = new JArray(Suggest(typeName, available))
                };
                return false;
            }

            // Several assemblies can define the same simple name; prefer the active node library,
            // then the core, then anything else. The chosen full name is returned to the caller so
            // the choice is never a surprise.
            matches.Sort((left, right) =>
            {
                int byPriority = Priority(left).CompareTo(Priority(right));
                if (byPriority != 0) return byPriority;

                return string.Compare(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);
            });

            type = matches[0];
            return true;
        }

        private static int Priority(Type type)
        {
            string assembly = type.Assembly.GetName().Name;
            string nodeAssembly = "";

            try
            {
                nodeAssembly = PluginHandler.Plugin == null ? "" : PluginHandler.Plugin.NodeAssemblyName ?? "";
            }
            catch (Exception) { }

            if (!string.IsNullOrEmpty(nodeAssembly) && string.Equals(assembly, nodeAssembly, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.Equals(assembly, CoreAssemblyName, StringComparison.OrdinalIgnoreCase))
                return 1;

            return 2;
        }

        private static string[] Suggest(string typeName, List<Type> available)
        {
            return available
                .Select(t => t.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => n.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n.Length)
                .Take(20)
                .ToArray();
        }

        private static string ValueToString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "";

            if (token is JValue value) return value.ToString();

            return token.ToString(Formatting.None);
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception.InnerException != null) exception = exception.InnerException;
            return exception;
        }
    }
}
