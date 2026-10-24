using System;
using LuaSTGEditorSharp;
using LuaSTGEditorSharp.EditorData;
using LuaSTGMcpBridge.Protocol;
using LuaSTGMcpBridge.Threading;
using Newtonsoft.Json.Linq;

namespace LuaSTGMcpBridge.Editor
{
    /// <summary>
    /// Read access to the editor that is safe to use from a socket handler.
    /// </summary>
    /// <remarks>
    /// The active document is found through <see cref="IMainWindow.ActivatedWorkSpaceData"/>,
    /// which reads the selected tab of the main window. That is a WPF read, so it must happen on
    /// the UI thread; every member here marshals through <see cref="UiThread"/> for you.
    /// <para/>
    /// This is the intended home of the later MCP commands (get tree, edit attribute, insert /
    /// delete node): they must all go through <see cref="UiThread"/> and, for changes, through
    /// <see cref="DocumentData.AddAndExecuteCommand"/> so that undo/redo and the bound
    /// <c>TreeView</c> stay consistent.
    /// </remarks>
    public static class EditorContext
    {
        /// <summary>
        /// Gets the main window as the editor contract. Must be read on the UI thread.
        /// </summary>
        public static IMainWindow MainWindow
        {
            get
            {
                System.Windows.Application application = System.Windows.Application.Current;
                return application == null ? null : application.MainWindow as IMainWindow;
            }
        }

        /// <summary>
        /// Gets the document of the selected tab. Must be read on the UI thread;
        /// use <see cref="GetActiveDocument"/> from a socket handler instead.
        /// </summary>
        public static DocumentData ActiveDocument
        {
            get
            {
                IMainWindow window = MainWindow;
                return window == null ? null : window.ActivatedWorkSpaceData;
            }
        }

        /// <summary>
        /// Gets the document of the selected tab, marshalled onto the UI thread.
        /// </summary>
        /// <returns>The active document, or <c>null</c> when no document is open.</returns>
        public static DocumentData GetActiveDocument()
        {
            return UiThread.Invoke(() => ActiveDocument);
        }

        /// <summary>
        /// Runs <paramref name="work"/> against the active document on the UI thread.
        /// </summary>
        /// <typeparam name="T">Return type of the work.</typeparam>
        /// <param name="work">Receives the active document, which may be <c>null</c>.</param>
        /// <returns>The value produced by <paramref name="work"/>.</returns>
        public static T WithActiveDocument<T>(Func<DocumentData, T> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            return UiThread.Invoke(() => work(ActiveDocument));
        }

        /// <summary>
        /// Describes the active document as the <c>editor_state</c> response.
        /// </summary>
        /// <returns>
        /// <c>{"status":"ok","editor":"connected","document":{...}}</c>, or a failure when no
        /// document is open.
        /// </returns>
        public static JObject DescribeActiveDocument()
        {
            return UiThread.Invoke(() =>
            {
                DocumentData document = ActiveDocument;

                if (document == null)
                    return BridgeProtocol.Failure("no document is open", "no_document");

                return new JObject
                {
                    ["status"] = "ok",
                    ["editor"] = "connected",
                    ["document"] = new JObject
                    {
                        ["name"] = document.RawDocName ?? "",
                        ["path"] = document.DocPath ?? "",
                        ["extension"] = document.Extension ?? "",
                        ["dirty"] = document.IsUnsaved,
                        ["nodeCount"] = CountNodes(document)
                    }
                };
            });
        }

        /// <summary>
        /// Counts the nodes of a document, root included. Must run on the UI thread.
        /// </summary>
        /// <param name="document">The document to inspect.</param>
        /// <returns>The number of nodes.</returns>
        public static int CountNodes(DocumentData document)
        {
            if (document == null || document.TreeNodes == null || document.TreeNodes.Count == 0)
                return 0;

            return CountNode(document.TreeNodes[0]);
        }

        private static int CountNode(TreeNode node)
        {
            if (node == null) return 0;

            int count = 1;
            foreach (TreeNode child in node.Children) count += CountNode(child);
            return count;
        }
    }
}
