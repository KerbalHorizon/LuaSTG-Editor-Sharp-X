using System;
using System.ComponentModel;
using LuaSTGEditorSharp;
using LuaSTGEditorSharp.EditorData;
using LuaSTGEditorSharp.EditorData.Commands;
using LuaSTGMcpBridge.Editor;
using LuaSTGMcpBridge.Protocol;
using LuaSTGMcpBridge.Threading;
using Newtonsoft.Json.Linq;

namespace LuaSTGMcpBridge.Actions
{
    /// <summary>
    /// The four tree commands: <c>get_tree</c>, <c>set_attribute</c>, <c>insert_node</c>,
    /// <c>delete_node</c>.
    /// </summary>
    /// <remarks>
    /// Handlers run on a socket thread, so every one of them wraps its whole body in
    /// <see cref="UiThread.Invoke"/>: path resolution, node lookup and the mutation all happen on
    /// the UI thread, which owns the bound collections.
    /// <para/>
    /// Nothing is ever changed through a backing field. Insertions and deletions go through the
    /// real <see cref="Command"/> types and <see cref="DocumentData.AddAndExecuteCommand"/>, and an
    /// attribute change goes through <see cref="EditAttrCommand"/>, so the editor's Undo and Redo
    /// buttons work on every change the bridge makes, and the bound <c>TreeView</c> updates itself
    /// through <c>ObservableCollection</c> and <c>INotifyPropertyChanged</c>.
    /// </remarks>
    public static class TreeActions
    {
        // ------------------------------------------------------------------ get_tree

        /// <summary>
        /// Returns the active document's tree.
        /// </summary>
        /// <param name="request">Optional <c>path</c>, <c>maxDepth</c> (0 = all), <c>attributes</c> (default true).</param>
        /// <returns>The response object.</returns>
        public static JObject GetTree(JObject request)
        {
            string[] missing = EditorCompat.GetMissing("get_tree");
            if (missing.Length > 0) return EditorCompat.Unsupported("get_tree", missing);

            return Guard("get_tree", () => GetTreeCore(request));
        }

        private static JObject GetTreeCore(JObject request)
        {
            return UiThread.Invoke(() =>
            {
                DocumentData document;
                JObject failure = RequireDocument(out document);
                if (failure != null) return failure;

                string path = BridgeProtocol.ReadString(request, "path");
                int maxDepth = BridgeProtocol.ReadInt(request, "maxDepth") ?? NodeSnapshot.UnlimitedDepth;
                bool includeAttributes = BridgeProtocol.ReadBool(request, "attributes", true);

                TreeNode node;
                int[] indexes;
                string error;

                if (!NodePath.TryResolve(document, path, out node, out indexes, out error))
                {
                    return BridgeProtocol.Failure(error, "node_not_found", new JObject
                    {
                        ["path"] = string.IsNullOrWhiteSpace(path) ? NodePath.RootPath : path
                    });
                }

                JObject response = new JObject
                {
                    ["status"] = "ok",
                    ["document"] = new JObject
                    {
                        ["name"] = document.RawDocName ?? "",
                        ["path"] = document.DocPath ?? "",
                        ["extension"] = document.Extension ?? "",
                        ["dirty"] = document.IsUnsaved,
                        ["nodeCount"] = EditorContext.CountNodes(document)
                    },
                    ["tree"] = NodeSnapshot.Capture(node, NodePath.Format(indexes), maxDepth, includeAttributes)
                };

                return response;
            });
        }

        // ------------------------------------------------------------ set_attribute

        /// <summary>
        /// Changes one attribute of one node through <see cref="EditAttrCommand"/>.
        /// </summary>
        /// <param name="request">Requires <c>path</c>, <c>name</c> and <c>value</c>; optional <c>reveal</c>.</param>
        /// <returns>The response object.</returns>
        public static JObject SetAttribute(JObject request)
        {
            string[] missing = EditorCompat.GetMissing("set_attribute");
            if (missing.Length > 0) return EditorCompat.Unsupported("set_attribute", missing);

            return Guard("set_attribute", () => SetAttributeCore(request));
        }

        private static JObject SetAttributeCore(JObject request)
        {
            return UiThread.Invoke(() =>
            {
                DocumentData document;
                JObject failure = RequireDocument(out document);
                if (failure != null) return failure;

                string path = BridgeProtocol.ReadString(request, "path");
                string name = BridgeProtocol.ReadString(request, "name");

                if (string.IsNullOrWhiteSpace(name))
                    return BridgeProtocol.Failure("'name' is required, for example \"Comment\"", "bad_request");

                JToken valueToken = request == null ? null : request["value"];
                if (valueToken == null)
                    return BridgeProtocol.Failure("'value' is required", "bad_request");

                TreeNode node;
                int[] indexes;
                string error;

                if (!NodePath.TryResolve(document, path, out node, out indexes, out error))
                {
                    return BridgeProtocol.Failure(error, "node_not_found", new JObject
                    {
                        ["path"] = string.IsNullOrWhiteSpace(path) ? NodePath.RootPath : path
                    });
                }

                AttrItem attribute = NodeFactory.FindAttribute(node, name);

                if (attribute == null)
                {
                    return BridgeProtocol.Failure(
                        "node '" + NodePath.Format(indexes) + "' (" + node.GetType().Name
                            + ") has no attribute '" + name + "'",
                        "unknown_attribute",
                        new JObject
                        {
                            ["path"] = NodePath.Format(indexes),
                            ["nodeType"] = node.GetType().Name,
                            ["availableAttributes"] = new JArray(NodeFactory.GetAttributeNames(node))
                        });
                }

                string newValue = valueToken.Type == JTokenType.Null ? "" : valueToken.ToString();
                string oldValue = attribute.AttrInput ?? "";

                if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    // No command, so the undo stack and the dirty flag stay untouched.
                    return new JObject
                    {
                        ["status"] = "ok",
                        ["path"] = NodePath.Format(indexes),
                        ["name"] = attribute.AttrCap,
                        ["oldValue"] = oldValue,
                        ["newValue"] = newValue,
                        ["changed"] = false,
                        ["text"] = NodeSnapshot.Describe(node)
                    };
                }

                // The command captures the current value as its undo state.
                document.AddAndExecuteCommand(new EditAttrCommand(attribute, oldValue, newValue));

                if (BridgeProtocol.ReadBool(request, "reveal", false)) TryReveal(node);

                return new JObject
                {
                    ["status"] = "ok",
                    ["path"] = NodePath.Format(indexes),
                    ["name"] = attribute.AttrCap,
                    ["oldValue"] = oldValue,
                    ["newValue"] = newValue,
                    ["changed"] = true,
                    ["text"] = NodeSnapshot.Describe(node)
                };
            });
        }

        // --------------------------------------------------------------- insert_node

        /// <summary>
        /// Inserts a new node under a parent, or next to a sibling, through the matching
        /// <see cref="InsertCommand"/>.
        /// </summary>
        /// <param name="request">
        /// Requires <c>type</c>; locates with <c>parent</c> (plus optional <c>index</c>) or with
        /// <c>after</c> / <c>before</c>; optional <c>attributes</c> and <c>reveal</c> (default true).
        /// </param>
        /// <returns>The response object.</returns>
        public static JObject InsertNode(JObject request)
        {
            string[] missing = EditorCompat.GetMissing("insert_node");
            if (missing.Length > 0) return EditorCompat.Unsupported("insert_node", missing);

            return Guard("insert_node", () => InsertNodeCore(request));
        }

        private static JObject InsertNodeCore(JObject request)
        {
            return UiThread.Invoke(() =>
            {
                DocumentData document;
                JObject failure = RequireDocument(out document);
                if (failure != null) return failure;

                string typeName = BridgeProtocol.ReadString(request, "type");
                if (string.IsNullOrWhiteSpace(typeName))
                    return BridgeProtocol.Failure("'type' is required, for example \"Comment\"", "bad_request");

                TreeNode newNode;
                string code;
                string message;
                JObject details;

                // Build the node before touching the tree: a validation failure then leaves the
                // document exactly as it was.
                if (!NodeFactory.TryCreate(typeName, document, BridgeProtocol.ReadObject(request, "attributes"),
                        out newNode, out code, out message, out details))
                {
                    return BridgeProtocol.Failure(message, code, details);
                }

                string parentPath = BridgeProtocol.ReadString(request, "parent");
                string afterPath = BridgeProtocol.ReadString(request, "after");
                string beforePath = BridgeProtocol.ReadString(request, "before");
                int? requestedIndex = BridgeProtocol.ReadInt(request, "index");

                InsertCommand command;
                string targetParentPath;
                int insertIndex;

                if (!string.IsNullOrWhiteSpace(afterPath) || !string.IsNullOrWhiteSpace(beforePath))
                {
                    // Sibling mode: the anchor's parent receives the node, right next to the anchor.
                    string anchorPath = !string.IsNullOrWhiteSpace(afterPath) ? afterPath : beforePath;
                    bool isAfter = !string.IsNullOrWhiteSpace(afterPath);

                    TreeNode anchor;
                    int[] anchorIndexes;
                    string error;

                    if (!NodePath.TryResolve(document, anchorPath, out anchor, out anchorIndexes, out error))
                    {
                        return BridgeProtocol.Failure(error, "node_not_found", new JObject { ["path"] = anchorPath });
                    }

                    if (anchor.Parent == null)
                    {
                        return BridgeProtocol.Failure(
                            "cannot insert next to the root node; use 'parent' with an 'index' instead",
                            "validation_failed",
                            new JObject { ["path"] = anchorPath });
                    }

                    if (!anchor.Parent.ValidateChild(newNode))
                        return ValidationFailed(newNode, anchor.Parent, NodePath.FormatParent(anchorIndexes));

                    command = isAfter
                        ? (InsertCommand)new InsertAfterCommand(anchor, newNode)
                        : new InsertBeforeCommand(anchor, newNode);

                    targetParentPath = NodePath.FormatParent(anchorIndexes);
                    insertIndex = anchorIndexes[anchorIndexes.Length - 1] + (isAfter ? 1 : 0);
                }
                else
                {
                    // Parent mode: append or insert at a child index.
                    string requestedParentPath = string.IsNullOrWhiteSpace(parentPath) ? NodePath.RootPath : parentPath;

                    TreeNode parent;
                    int[] parentIndexes;
                    string error;

                    if (!NodePath.TryResolve(document, requestedParentPath, out parent, out parentIndexes, out error))
                    {
                        return BridgeProtocol.Failure(error, "node_not_found", new JObject { ["path"] = requestedParentPath });
                    }

                    if (!parent.ValidateChild(newNode))
                        return ValidationFailed(newNode, parent, NodePath.Format(parentIndexes));

                    int childCount = parent.Children == null ? 0 : parent.Children.Count;
                    int targetIndex = requestedIndex ?? childCount;

                    if (targetIndex < 0 || targetIndex > childCount)
                    {
                        return BridgeProtocol.Failure(
                            "index " + targetIndex + " is out of range 0.." + childCount,
                            "index_out_of_range",
                            new JObject { ["path"] = NodePath.Format(parentIndexes), ["childCount"] = childCount });
                    }

                    if (targetIndex >= childCount)
                        command = new InsertAsChildCommand(parent, newNode);
                    else if (targetIndex == 0)
                        command = new InsertBeforeCommand(parent.Children[0], newNode);
                    else
                        command = new InsertAfterCommand(parent.Children[targetIndex - 1], newNode);

                    targetParentPath = NodePath.Format(parentIndexes);
                    insertIndex = targetIndex;
                }

                if (!document.AddAndExecuteCommand(command))
                    return BridgeProtocol.Failure("the editor rejected the insert command", "insert_failed");

                // The node was configured while detached, so recompute its validation messages now
                // that it is part of the tree. MainWindow.Insert does the same after an insert.
                newNode.CheckMessage(null, new PropertyChangedEventArgs(""));

                // Make the change visible: expands the ancestors and selects the new row.
                bool revealed = BridgeProtocol.ReadBool(request, "reveal", true) && TryReveal(newNode);

                return new JObject
                {
                    ["status"] = "ok",
                    ["path"] = NodePath.Format(newNode),
                    ["type"] = newNode.GetType().Name,
                    ["typeFullName"] = newNode.GetType().FullName,
                    ["text"] = NodeSnapshot.Describe(newNode),
                    ["parentPath"] = targetParentPath,
                    ["index"] = insertIndex,
                    ["selected"] = revealed,
                    ["nodeCount"] = EditorContext.CountNodes(document)
                };
            });
        }

        // --------------------------------------------------------------- delete_node

        /// <summary>
        /// Deletes a node through <see cref="DeleteCommand"/>.
        /// </summary>
        /// <param name="request">Requires <c>path</c>; optional <c>reveal</c> (default true).</param>
        /// <returns>The response object.</returns>
        public static JObject DeleteNode(JObject request)
        {
            string[] missing = EditorCompat.GetMissing("delete_node");
            if (missing.Length > 0) return EditorCompat.Unsupported("delete_node", missing);

            return Guard("delete_node", () => DeleteNodeCore(request));
        }

        private static JObject DeleteNodeCore(JObject request)
        {
            return UiThread.Invoke(() =>
            {
                DocumentData document;
                JObject failure = RequireDocument(out document);
                if (failure != null) return failure;

                string path = BridgeProtocol.ReadString(request, "path");

                TreeNode node;
                int[] indexes;
                string error;

                if (!NodePath.TryResolve(document, path, out node, out indexes, out error))
                {
                    return BridgeProtocol.Failure(error, "node_not_found", new JObject
                    {
                        ["path"] = string.IsNullOrWhiteSpace(path) ? NodePath.RootPath : path
                    });
                }

                string nodePath = NodePath.Format(indexes);

                if (node.Parent == null)
                {
                    return BridgeProtocol.Failure(
                        "the root node cannot be deleted",
                        "cannot_delete",
                        new JObject { ["path"] = nodePath });
                }

                if (!NodeSnapshot.CanDelete(node))
                {
                    return BridgeProtocol.Failure(
                        "node type '" + node.GetType().Name + "' is marked [CannotDelete]",
                        "cannot_delete",
                        new JObject { ["path"] = nodePath, ["type"] = node.GetType().Name });
                }

                // Capture what the caller needs before the node leaves the tree.
                TreeNode parent = node.Parent;
                string typeName = node.GetType().Name;
                string text = NodeSnapshot.Describe(node);
                string parentPath = NodePath.FormatParent(indexes);
                int index = indexes[indexes.Length - 1];

                if (!document.AddAndExecuteCommand(new DeleteCommand(node)))
                    return BridgeProtocol.Failure("the editor rejected the delete command", "delete_failed");

                if (BridgeProtocol.ReadBool(request, "reveal", true) && parent.Parent != null)
                    TryReveal(parent);

                return new JObject
                {
                    ["status"] = "ok",
                    ["path"] = nodePath,
                    ["type"] = typeName,
                    ["text"] = text,
                    ["parentPath"] = parentPath,
                    ["index"] = index,
                    ["nodeCount"] = EditorContext.CountNodes(document)
                };
            });
        }

        // -------------------------------------------------------------------- shared

        private static JObject RequireDocument(out DocumentData document)
        {
            document = EditorContext.ActiveDocument;

            if (document == null)
                return BridgeProtocol.Failure("no document is open", "no_document");

            if (document.TreeNodes == null || document.TreeNodes.Count == 0)
                return BridgeProtocol.Failure("the active document has no root node yet", "no_tree");

            return null;
        }

        private static JObject ValidationFailed(TreeNode node, TreeNode parent, string parentPath)
        {
            return BridgeProtocol.Failure(
                "'" + node.GetType().Name + "' cannot be inserted under '" + parent.GetType().Name + "'",
                "validation_failed",
                new JObject
                {
                    ["parentPath"] = parentPath,
                    ["parentType"] = parent.GetType().Name,
                    ["nodeType"] = node.GetType().Name
                });
        }

        /// <summary>
        /// Runs one action and converts any unexpected exception into an error response.
        /// </summary>
        /// <remarks>
        /// The TCP server already guards its request loop. This second layer is deliberate:
        /// a version-mismatched editor can throw from property getters or command constructors
        /// that the handler does not expect, and such a failure must never escape into the WPF
        /// dispatcher or the process. Logging is guarded as well, because Serilog itself may be
        /// the very assembly that is missing on an older editor.
        /// </remarks>
        /// <param name="action">Action name used in the log and in the error response.</param>
        /// <param name="handler">The guarded handler.</param>
        /// <returns>The handler result, or an error response.</returns>
        private static JObject Guard(string action, Func<JObject> handler)
        {
            try
            {
                return handler();
            }
            catch (Exception ex)
            {
                try
                {
                    BridgeFileLog.CreateLogger("McpTreeActions")
                        .Error(ex, "MCP bridge action {Action} failed.", action);
                }
                catch (Exception)
                {
                    // Logging must never turn a handled failure into an editor crash.
                }

                return BridgeProtocol.Failure(
                    "internal error in '" + action + "': " + ex.Message, "error");
            }
        }

        private static bool TryReveal(TreeNode node)
        {
            try
            {
                IMainWindow window = EditorContext.MainWindow;
                if (window == null || node == null) return false;

                window.Reveal(node);
                return true;
            }
            catch (Exception)
            {
                // Revealing is cosmetic: never fail a command because the view could not scroll.
                return false;
            }
        }
    }
}
