using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using LuaSTGMcpBridge.Protocol;
using Newtonsoft.Json.Linq;

namespace LuaSTGMcpBridge.Editor
{
    /// <summary>
    /// Runtime capability probe for the editor APIs the bridge uses.
    /// </summary>
    /// <remarks>
    /// The bridge is compiled against the v0.78.4 Core, but the official v0.78.0-v0.78.3 editors
    /// bind it by simple name (the Core assembly is not strong-named). This probe is the safety
    /// net: before an action touches a Core member, we verify that the member exists with the
    /// expected shape. A missing member becomes a clear <c>unsupported_editor_version</c> error
    /// instead of a <see cref="MissingMethodException"/> during JIT.
    /// <para/>
    /// The probe uses reflection only, never <c>typeof(DocumentData)</c>, so the probe itself can
    /// run on an editor that is missing one of the optional members.
    /// </remarks>
    internal static class EditorCompat
    {
        private const string CoreAssemblyName = "LuaSTGEditorSharp.Core";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string[]> Cache =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        private static string hostVersion;
        private static Assembly coreAssembly;
        private static bool coreAssemblySearched;
        private static string engineName;
        private static string pluginName;

        internal static string HostVersion
        {
            get
            {
                EnsureHostVersion();
                return hostVersion;
            }
        }

        internal static bool IsCompatible
        {
            get
            {
                return GetMissing("editor_state").Length == 0
                    && GetMissing("get_tree").Length == 0
                    && GetMissing("set_attribute").Length == 0
                    && GetMissing("insert_node").Length == 0
                    && GetMissing("delete_node").Length == 0;
            }
        }

        /// <summary>
        /// Gets the LuaSTG flavour of the loaded plugin, for example "LuaSTG Sub" or
        /// "LuaSTG ExPlus", read from <c>AbstractPluginEntry.TargetLSTGVersion</c>.
        /// </summary>
        /// <remarks>
        /// Never hard coded: the same bridge binary has to report whichever node library the
        /// editor actually loaded. While the plugin constructor is still running the handler
        /// holds no instance yet, so this answers "unknown" and retries on the next call.
        /// </remarks>
        internal static string EngineName
        {
            get
            {
                EnsurePluginIdentity();
                return engineName ?? "unknown";
            }
        }

        /// <summary>
        /// Gets the simple name of the loaded plugin assembly, for example "LuaSTGSubLib".
        /// </summary>
        internal static string PluginName
        {
            get
            {
                EnsurePluginIdentity();
                return pluginName ?? "unknown";
            }
        }

        internal static string[] GetMissing(string action)
        {
            if (string.IsNullOrEmpty(action)) action = "unknown";

            lock (Gate)
            {
                string[] cached;
                if (Cache.TryGetValue(action, out cached)) return cached;

                List<string> missing = new List<string>();
                try
                {
                    Probe(action, missing);
                }
                catch (Exception ex)
                {
                    missing.Add("capability probe failed: " + ex.Message);
                }

                string[] result = missing.ToArray();
                Cache[action] = result;
                return result;
            }
        }

        internal static JObject Unsupported(string action, string[] missing)
        {
            JObject details = new JObject
            {
                ["action"] = action,
                ["editorVersion"] = HostVersion
            };

            JArray missingArray = new JArray();
            if (missing != null)
            {
                foreach (string member in missing) missingArray.Add(member);
            }
            details["missing"] = missingArray;

            return BridgeProtocol.Failure(
                "editor " + HostVersion + " does not expose the API required by '" + action + "'",
                "unsupported_editor_version",
                details);
        }

        private static void Probe(string action, List<string> missing)
        {
            Type document = CoreType("LuaSTGEditorSharp.EditorData.DocumentData");
            Type treeNode = CoreType("LuaSTGEditorSharp.EditorData.TreeNode");
            Type attr = CoreType("LuaSTGEditorSharp.EditorData.AttrItem");
            Type command = CoreType("LuaSTGEditorSharp.EditorData.Command");
            Type mainWindow = CoreType("LuaSTGEditorSharp.IMainWindow");

            NeedType(document, "DocumentData", missing);
            NeedType(treeNode, "TreeNode", missing);
            NeedType(attr, "AttrItem", missing);
            NeedType(command, "Command", missing);
            NeedType(mainWindow, "IMainWindow", missing);

            if (document != null)
            {
                NeedProperty(document, "RawDocName", missing);
                NeedProperty(document, "DocPath", missing);
                NeedProperty(document, "Extension", missing);
                NeedProperty(document, "IsUnsaved", missing);
                NeedProperty(document, "TreeNodes", missing);

                PropertyInfo treeNodes = document.GetProperty("TreeNodes", BindingFlags.Public | BindingFlags.Instance);
                if (treeNodes != null)
                {
                    NeedProperty(treeNodes.PropertyType, "Count", missing);
                    NeedProperty(treeNodes.PropertyType, "Item", missing);
                }
            }

            if (treeNode != null)
            {
                NeedProperty(treeNode, "Attributes", missing);
                NeedProperty(treeNode, "Children", missing);
                NeedProperty(treeNode, "Parent", missing);
            }

            if (attr != null)
            {
                NeedProperty(attr, "AttrCap", missing);
                NeedProperty(attr, "AttrInput", missing);
            }

            if (mainWindow != null)
            {
                NeedProperty(mainWindow, "ActivatedWorkSpaceData", missing);
            }

            bool isGetTree = Is(action, "get_tree");
            bool isSetAttribute = Is(action, "set_attribute");
            bool isInsertNode = Is(action, "insert_node");
            bool isDeleteNode = Is(action, "delete_node");
            bool isWrite = isSetAttribute || isInsertNode || isDeleteNode;

            if (isGetTree)
            {
                NeedProperty(treeNode, "IsBanned", missing);
                NeedProperty(treeNode, "IsExpanded", missing);
                NeedProperty(treeNode, "IsSelected", missing);
                NeedProperty(treeNode, "CanDelete", missing);
                NeedProperty(attr, "EditWindow", missing);
            }

            if (isWrite && document != null && command != null)
            {
                NeedMethod(document, "AddAndExecuteCommand", new[] { command }, missing, typeof(bool));
            }

            if (isWrite && mainWindow != null && treeNode != null)
            {
                NeedMethod(mainWindow, "Reveal", new[] { treeNode }, missing, typeof(void));
            }

            if (isSetAttribute)
            {
                Type editAttr = CoreType("LuaSTGEditorSharp.EditorData.Commands.EditAttrCommand");
                NeedType(editAttr, "EditAttrCommand", missing);
                if (editAttr != null && attr != null)
                {
                    NeedCtor(editAttr, new[] { attr, typeof(string), typeof(string) }, missing);
                }
            }

            if (isInsertNode)
            {
                NeedMethod(treeNode, "ValidateChild", new[] { treeNode }, missing, typeof(bool));
                NeedMethod(treeNode, "CheckMessage",
                    new[] { typeof(object), typeof(PropertyChangedEventArgs) }, missing, typeof(void));
                NeedField(treeNode, "parentWorkSpace", missing);

                Type after = CoreType("LuaSTGEditorSharp.EditorData.Commands.InsertAfterCommand");
                Type before = CoreType("LuaSTGEditorSharp.EditorData.Commands.InsertBeforeCommand");
                Type asChild = CoreType("LuaSTGEditorSharp.EditorData.Commands.InsertAsChildCommand");
                NeedType(after, "InsertAfterCommand", missing);
                NeedType(before, "InsertBeforeCommand", missing);
                NeedType(asChild, "InsertAsChildCommand", missing);

                if (after != null && treeNode != null) NeedCtor(after, new[] { treeNode, treeNode }, missing);
                if (before != null && treeNode != null) NeedCtor(before, new[] { treeNode, treeNode }, missing);
                if (asChild != null && treeNode != null) NeedCtor(asChild, new[] { treeNode, treeNode }, missing);

                Type pluginHandler = CoreType("LuaSTGEditorSharp.Plugin.PluginHandler");
                Type pluginEntry = CoreType("LuaSTGEditorSharp.Plugin.AbstractPluginEntry");
                Type nodeCache = CoreType("LuaSTGEditorSharp.EditorData.Node.AbstractNodeTypeCache");
                NeedType(pluginHandler, "PluginHandler", missing);
                NeedType(pluginEntry, "AbstractPluginEntry", missing);
                NeedType(nodeCache, "AbstractNodeTypeCache", missing);

                NeedProperty(pluginHandler, "Plugin", missing, true);
                NeedProperty(pluginEntry, "NodeTypeCache", missing);
                NeedProperty(pluginEntry, "NodeAssemblyName", missing);
                NeedProperty(nodeCache, "NodeTypes", missing);
            }

            if (isDeleteNode)
            {
                NeedProperty(treeNode, "CanDelete", missing);

                Type delete = CoreType("LuaSTGEditorSharp.EditorData.Commands.DeleteCommand");
                NeedType(delete, "DeleteCommand", missing);
                if (delete != null && treeNode != null)
                {
                    NeedCtor(delete, new[] { treeNode }, missing);
                }
            }
        }

        private static bool Is(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static void NeedType(Type type, string name, List<string> missing)
        {
            if (type == null) missing.Add("type " + name);
        }

        private static void NeedProperty(Type owner, string name, List<string> missing, bool isStatic = false)
        {
            if (owner == null)
            {
                missing.Add(name + " (owner type missing)");
                return;
            }

            BindingFlags flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            if (owner.GetProperty(name, flags) == null)
            {
                missing.Add(owner.FullName + "." + name);
            }
        }

        private static void NeedField(Type owner, string name, List<string> missing)
        {
            if (owner == null)
            {
                missing.Add(name + " (owner type missing)");
                return;
            }

            if (owner.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) == null)
            {
                missing.Add(owner.FullName + "." + name);
            }
        }

        private static void NeedMethod(Type owner, string name, Type[] parameters, List<string> missing, Type returnType)
        {
            if (owner == null)
            {
                missing.Add(name + " (owner type missing)");
                return;
            }
            if (parameters == null || Array.IndexOf(parameters, null) >= 0)
            {
                missing.Add(owner.FullName + "." + name + " (parameter type missing)");
                return;
            }

            MethodInfo method;
            try
            {
                method = owner.GetMethod(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static,
                    null, parameters, null);
            }
            catch (Exception)
            {
                method = null;
            }

            if (method == null)
            {
                missing.Add(owner.FullName + "." + name + "(" + Describe(parameters) + ")");
                return;
            }

            if (returnType != null && method.ReturnType != returnType)
            {
                missing.Add(owner.FullName + "." + name + " returns " + method.ReturnType.Name
                    + ", expected " + returnType.Name);
            }
        }

        private static void NeedCtor(Type owner, Type[] parameters, List<string> missing)
        {
            if (owner == null)
            {
                missing.Add("constructor owner type missing");
                return;
            }
            if (parameters == null || Array.IndexOf(parameters, null) >= 0)
            {
                missing.Add(owner.FullName + "..ctor (parameter type missing)");
                return;
            }

            ConstructorInfo ctor;
            try
            {
                ctor = owner.GetConstructor(parameters);
            }
            catch (Exception)
            {
                ctor = null;
            }

            if (ctor == null)
            {
                missing.Add(owner.FullName + "..ctor(" + Describe(parameters) + ")");
            }
        }

        private static string Describe(Type[] parameters)
        {
            if (parameters == null) return "";

            string[] names = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                names[i] = parameters[i] == null ? "?" : parameters[i].Name;
            }

            return string.Join(", ", names);
        }

        private static Type CoreType(string fullName)
        {
            Assembly assembly = FindCoreAssembly();
            return assembly == null ? null : assembly.GetType(fullName, false);
        }

        private static Assembly FindCoreAssembly()
        {
            if (coreAssembly != null) return coreAssembly;
            if (coreAssemblySearched) return null;
            coreAssemblySearched = true;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, CoreAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    coreAssembly = assembly;
                    return assembly;
                }
            }

            try
            {
                coreAssembly = Assembly.Load(CoreAssemblyName);
            }
            catch (Exception)
            {
                coreAssembly = null;
            }

            return coreAssembly;
        }

        private static void EnsurePluginIdentity()
        {
            if (engineName != null && pluginName != null) return;

            lock (Gate)
            {
                if (engineName != null && pluginName != null) return;

                try
                {
                    Type handler = CoreType("LuaSTGEditorSharp.Plugin.PluginHandler");
                    PropertyInfo pluginProperty = handler == null
                        ? null
                        : handler.GetProperty("Plugin", BindingFlags.Public | BindingFlags.Static);

                    object plugin = pluginProperty == null ? null : pluginProperty.GetValue(null, null);

                    // PluginHandler assigns the instance only after CreateInstance returns, so a
                    // null here just means the plugin constructor is still running. Not cached.
                    if (plugin == null) return;

                    pluginName = plugin.GetType().Assembly.GetName().Name;

                    PropertyInfo target = plugin.GetType().GetProperty("TargetLSTGVersion");
                    if (target != null) engineName = target.GetValue(plugin, null) as string;

                    if (string.IsNullOrEmpty(engineName)) engineName = "unknown";
                    if (string.IsNullOrEmpty(pluginName)) pluginName = "unknown";
                }
                catch (Exception)
                {
                    // The probe must never break the ping handshake.
                }
            }
        }

        private static void EnsureHostVersion()
        {
            if (hostVersion != null) return;

            lock (Gate)
            {
                if (hostVersion != null) return;

                try
                {
                    Assembly entry = Assembly.GetEntryAssembly();
                    if (entry != null)
                    {
                        Version version = entry.GetName().Version;
                        if (version != null)
                        {
                            hostVersion = version.ToString();
                            return;
                        }
                    }
                }
                catch (Exception)
                {
                }

                try
                {
                    Assembly entry = Assembly.GetEntryAssembly();
                    if (entry != null)
                    {
                        hostVersion = FileVersionInfo.GetVersionInfo(entry.Location).FileVersion;
                    }
                }
                catch (Exception)
                {
                }

                if (string.IsNullOrEmpty(hostVersion)) hostVersion = "unknown";
            }
        }
    }
}
