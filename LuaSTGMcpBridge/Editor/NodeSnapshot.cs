using System;
using System.Collections.ObjectModel;
using LuaSTGEditorSharp.EditorData;
using Newtonsoft.Json.Linq;

namespace LuaSTGMcpBridge.Editor
{
    /// <summary>
    /// Turns a <see cref="TreeNode"/> into the JSON shape used by <c>get_tree</c>.
    /// </summary>
    /// <remarks>
    /// Read only: it walks the already attached tree, so it must run on the UI thread just like
    /// every other access to the bound collections.
    /// </remarks>
    public static class NodeSnapshot
    {
        /// <summary>
        /// Value of <c>maxDepth</c> meaning "no limit".
        /// </summary>
        public const int UnlimitedDepth = 0;

        /// <summary>
        /// Captures a node and its descendants.
        /// </summary>
        /// <param name="node">Node to capture.</param>
        /// <param name="path">Path of <paramref name="node"/>.</param>
        /// <param name="maxDepth">Levels to capture below the node, <see cref="UnlimitedDepth"/> for all.</param>
        /// <param name="includeAttributes">Whether to include attribute values.</param>
        /// <returns>The JSON object, or <c>null</c> when <paramref name="node"/> is <c>null</c>.</returns>
        public static JObject Capture(TreeNode node, string path, int maxDepth = UnlimitedDepth, bool includeAttributes = true)
        {
            if (node == null) return null;

            return CaptureNode(node, path ?? NodePath.RootPath, 0, Math.Max(0, maxDepth), includeAttributes);
        }

        /// <summary>
        /// Captures every attribute of a node.
        /// </summary>
        /// <param name="node">Node to inspect.</param>
        /// <returns>A JSON array, empty when the node has no attributes.</returns>
        public static JArray CaptureAttributes(TreeNode node)
        {
            JArray result = new JArray();
            ObservableCollection<AttrItem> attributes = AttributesOf(node);

            foreach (AttrItem attribute in attributes)
            {
                if (attribute == null) continue; // InsertAttrAt can pad the collection with nulls.

                result.Add(new JObject
                {
                    ["name"] = attribute.AttrCap ?? "",
                    ["value"] = attribute.AttrInput ?? "",
                    ["editWindow"] = attribute.EditWindow ?? ""
                });
            }

            return result;
        }

        /// <summary>
        /// Gets the text the editor shows for a node, tolerating nodes whose ToString throws
        /// because an attribute is missing.
        /// </summary>
        /// <param name="node">Node to describe.</param>
        /// <returns>Display text.</returns>
        public static string Describe(TreeNode node)
        {
            if (node == null) return "";

            try
            {
                return node.ToString() ?? "";
            }
            catch (Exception)
            {
                return node.GetType().Name;
            }
        }

        /// <summary>
        /// Reads the <c>canDelete</c> flag of a node type, tolerating an unknown type.
        /// </summary>
        /// <param name="node">Node to inspect.</param>
        /// <returns>The flag, or <c>true</c> when it cannot be determined.</returns>
        public static bool CanDelete(TreeNode node)
        {
            if (node == null) return false;

            try
            {
                return node.CanDelete;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static JObject CaptureNode(TreeNode node, string path, int depth, int maxDepth, bool includeAttributes)
        {
            ObservableCollection<TreeNode> children = ChildrenOf(node);

            JObject result = new JObject
            {
                ["path"] = path,
                ["type"] = node.GetType().Name,
                ["typeFullName"] = node.GetType().FullName,
                ["text"] = Describe(node),
                ["banned"] = node.IsBanned,
                ["expanded"] = node.IsExpanded,
                ["selected"] = node.IsSelected,
                ["canDelete"] = CanDelete(node),
                ["attributeCount"] = AttributesOf(node).Count,
                ["childCount"] = children.Count
            };

            if (includeAttributes)
            {
                result["attributes"] = CaptureAttributes(node);
            }

            bool truncated = maxDepth != UnlimitedDepth && depth >= maxDepth;
            JArray childArray = new JArray();

            if (!truncated)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    childArray.Add(CaptureNode(children[i], path + "/" + i, depth + 1, maxDepth, includeAttributes));
                }
            }
            else if (children.Count > 0)
            {
                result["truncated"] = true;
            }

            result["children"] = childArray;
            return result;
        }

        private static ObservableCollection<TreeNode> ChildrenOf(TreeNode node)
        {
            if (node == null) return new ObservableCollection<TreeNode>();
            return node.Children ?? new ObservableCollection<TreeNode>();
        }

        private static ObservableCollection<AttrItem> AttributesOf(TreeNode node)
        {
            if (node == null) return new ObservableCollection<AttrItem>();
            return node.Attributes ?? new ObservableCollection<AttrItem>();
        }
    }
}
