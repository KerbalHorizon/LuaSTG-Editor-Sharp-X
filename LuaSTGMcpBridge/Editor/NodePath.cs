using System;
using System.Collections.Generic;
using System.Globalization;
using LuaSTGEditorSharp.EditorData;

namespace LuaSTGMcpBridge.Editor
{
    /// <summary>
    /// Addresses tree nodes by a child-index path such as <c>0/2/1</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="LuaSTGEditorSharp.EditorData.TreeNode"/> has no identifier, so the only stable
    /// handle is the position in the tree. The first segment indexes
    /// <see cref="DocumentData.TreeNodes"/> (normally just <c>0</c>, the root folder), and every
    /// following segment indexes <see cref="TreeNode.Children"/>.
    /// <para/>
    /// A path stays valid only while the tree is unchanged: inserting or deleting a sibling shifts
    /// the indexes of everything after it, so a client must re-read the tree after a structural
    /// command.
    /// </remarks>
    public static class NodePath
    {
        /// <summary>
        /// Path of the document root.
        /// </summary>
        public const string RootPath = "0";

        /// <summary>
        /// Parses a path. An empty or missing path means the root.
        /// </summary>
        /// <param name="path">Path such as <c>0/2/1</c>.</param>
        /// <param name="indexes">Parsed segments.</param>
        /// <param name="error">Reason when parsing fails.</param>
        /// <returns><c>true</c> when the path is well formed.</returns>
        public static bool TryParse(string path, out int[] indexes, out string error)
        {
            indexes = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                indexes = new[] { 0 };
                return true;
            }

            string[] segments = path.Trim().Split('/');
            int[] parsed = new int[segments.Length];

            for (int i = 0; i < segments.Length; i++)
            {
                int value;

                if (!int.TryParse(segments[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                    || value < 0)
                {
                    error = "invalid path segment '" + segments[i] + "' in '" + path
                        + "'; a path looks like '0/2/1'";
                    return false;
                }

                parsed[i] = value;
            }

            indexes = parsed;
            return true;
        }

        /// <summary>
        /// Formats parsed segments back to a path.
        /// </summary>
        /// <param name="indexes">Segments to format.</param>
        /// <returns>The path, <see cref="RootPath"/> when empty.</returns>
        public static string Format(int[] indexes)
        {
            if (indexes == null || indexes.Length == 0) return RootPath;

            string[] parts = new string[indexes.Length];
            for (int i = 0; i < indexes.Length; i++)
            {
                parts[i] = indexes[i].ToString(CultureInfo.InvariantCulture);
            }

            return string.Join("/", parts);
        }

        /// <summary>
        /// Formats the parent of a path.
        /// </summary>
        /// <param name="indexes">Segments of the child.</param>
        /// <returns>The parent path, or <c>null</c> when <paramref name="indexes"/> is the root.</returns>
        public static string FormatParent(int[] indexes)
        {
            if (indexes == null || indexes.Length <= 1) return null;

            int[] parent = new int[indexes.Length - 1];
            Array.Copy(indexes, parent, parent.Length);
            return Format(parent);
        }

        /// <summary>
        /// Computes the path of an attached node by walking up to the root.
        /// </summary>
        /// <param name="node">A node that belongs to a document.</param>
        /// <returns>The path, or <c>null</c> when the node is not attached.</returns>
        public static string Format(TreeNode node)
        {
            if (node == null) return null;

            List<int> parts = new List<int>();
            TreeNode current = node;

            while (current.Parent != null)
            {
                int index = current.Parent.Children.IndexOf(current);
                if (index < 0) return null;

                parts.Add(index);
                current = current.Parent;
            }

            DocumentData document = current.parentWorkSpace;
            if (document == null || document.TreeNodes == null) return null;

            int rootIndex = document.TreeNodes.IndexOf(current);
            parts.Add(rootIndex < 0 ? 0 : rootIndex);

            parts.Reverse();
            return Format(parts.ToArray());
        }

        /// <summary>
        /// Resolves a path against a document.
        /// </summary>
        /// <param name="document">Document that owns the tree.</param>
        /// <param name="path">Path to resolve, empty for the root.</param>
        /// <param name="node">The resolved node.</param>
        /// <param name="indexes">Segments of the resolved path.</param>
        /// <param name="error">Reason when resolution fails.</param>
        /// <returns><c>true</c> when the node was found.</returns>
        public static bool TryResolve(DocumentData document, string path, out TreeNode node, out int[] indexes, out string error)
        {
            node = null;
            indexes = null;
            error = null;

            if (document == null)
            {
                error = "no document is open";
                return false;
            }

            if (!TryParse(path, out indexes, out error)) return false;

            if (document.TreeNodes == null || document.TreeNodes.Count == 0)
            {
                error = "the active document has no root node yet";
                return false;
            }

            if (indexes[0] >= document.TreeNodes.Count)
            {
                error = "no root node at index " + indexes[0]
                    + "; the document has " + document.TreeNodes.Count + " root node(s)";
                return false;
            }

            TreeNode current = document.TreeNodes[indexes[0]];

            for (int i = 1; i < indexes.Length; i++)
            {
                if (current.Children == null || indexes[i] >= current.Children.Count)
                {
                    error = "no child at index " + indexes[i] + " under '"
                        + Format(Slice(indexes, i)) + "'; that node has "
                        + (current.Children == null ? 0 : current.Children.Count) + " child(ren)";
                    indexes = null;
                    return false;
                }

                current = current.Children[indexes[i]];
            }

            node = current;
            return true;
        }

        private static int[] Slice(int[] source, int length)
        {
            int[] result = new int[length];
            Array.Copy(source, result, length);
            return result;
        }
    }
}
