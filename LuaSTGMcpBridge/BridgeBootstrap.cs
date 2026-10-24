using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace LuaSTGMcpBridge
{
    /// <summary>
    /// Installs a small, deliberately scoped assembly resolver for the bridge's private
    /// third-party dependencies.
    /// </summary>
    /// <remarks>
    /// The editor is shipped with its own Serilog/Newtonsoft.Json versions. When the bridge is
    /// dropped into an older editor, those versions can be older than the ones the bridge was
    /// built against. This resolver supplies the bridge's own copies from a <c>BridgeDeps</c>
    /// directory, without touching the host's <c>LuaSTGEditorSharp.Core</c> or node assemblies.
    /// <para/>
    /// IMPORTANT - this only runs when the CLR <b>fails</b> to bind an assembly. Both
    /// <c>Newtonsoft.Json</c> and <c>Serilog</c> have a <c>bindingRedirect</c> in
    /// <c>LuaSTGEditorSharp.exe.config</c>, and the same assembly version sits in <c>lib\</c>, so
    /// the bind usually succeeds against the host's build and no resolve event is raised at all.
    /// A newer build can add API without changing the assembly version (Newtonsoft.Json 13.0.4
    /// added <c>JToken.ToString(Formatting)</c> while still stamping 13.0.0.0), which produced a
    /// <c>MissingMethodException</c> in the field on a v0.78.3 host. That class of failure cannot
    /// be fixed here; it is fixed at the call site by
    /// <see cref="LuaSTGMcpBridge.Protocol.JsonCompat"/>, which sticks to API that predates 13.
    /// <para/>
    /// It must be installed before the first Serilog/Newtonsoft.Json type is touched. Do not add
    /// any Serilog or Newtonsoft.Json types to this file: the resolver itself has to stay loadable
    /// while those assemblies are still unresolved.
    /// </remarks>
    public static class BridgeBootstrap
    {
        private static readonly object Gate = new object();

        private static readonly HashSet<string> AllowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Serilog",
            "Newtonsoft.Json"
        };

        private static readonly HashSet<string> ResolvingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool installed;

        /// <summary>
        /// Registers the resolver once. Safe to call from every plugin entry path.
        /// </summary>
        /// <returns><c>true</c> when the resolver is installed.</returns>
        public static bool EnsureInstalled()
        {
            if (installed) return true;

            lock (Gate)
            {
                if (installed) return true;

                try
                {
                    AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                    installed = true;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            if (args == null || string.IsNullOrEmpty(args.Name)) return null;

            AssemblyName requested;
            try
            {
                requested = new AssemblyName(args.Name);
            }
            catch (Exception)
            {
                return null;
            }

            string simpleName = requested.Name;
            if (string.IsNullOrEmpty(simpleName) || !AllowedNames.Contains(simpleName)) return null;

            // Never override the host editor's Core/Node libraries. Only requests made by the
            // bridge (or its own dependency graph) are eligible.
            Assembly requesting = args.RequestingAssembly;
            if (requesting != null && !IsBridgeOwned(requesting)) return null;

            lock (Gate)
            {
                if (ResolvingNames.Contains(simpleName)) return null;

                ResolvingNames.Add(simpleName);
                try
                {
                    Assembly alreadyLoaded = FindLoaded(requested);
                    if (alreadyLoaded != null) return alreadyLoaded;

                    foreach (string directory in CandidateDirectories())
                    {
                        string path = Path.Combine(directory, simpleName + ".dll");
                        if (!File.Exists(path)) continue;

                        AssemblyName candidate;
                        try
                        {
                            candidate = AssemblyName.GetAssemblyName(path);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (!string.Equals(candidate.Name, simpleName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!PublicKeyTokenEquals(candidate, requested)) continue;
                        if (candidate.Version == null || requested.Version == null) continue;
                        if (candidate.Version.CompareTo(requested.Version) < 0) continue;

                        try
                        {
                            return Assembly.LoadFrom(path);
                        }
                        catch (FileLoadException)
                        {
                            // Another copy of the same identity may already be loaded.
                            Assembly fallback = FindLoaded(candidate);
                            if (fallback != null) return fallback;
                        }
                        catch (BadImageFormatException)
                        {
                            // Try the next candidate directory.
                        }
                    }
                }
                catch (Exception)
                {
                    // A resolver must never throw into the CLR; a missing dependency is reported
                    // later by the normal type-load failure and handled by the bridge guard.
                }
                finally
                {
                    ResolvingNames.Remove(simpleName);
                }
            }

            return null;
        }

        private static bool IsBridgeOwned(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            return string.Equals(name, "LuaSTGMcpBridge", StringComparison.OrdinalIgnoreCase)
                || AllowedNames.Contains(name);
        }

        private static Assembly FindLoaded(AssemblyName requested)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName name = assembly.GetName();

                if (!string.Equals(name.Name, requested.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!PublicKeyTokenEquals(name, requested)) continue;
                if (name.Version == null || requested.Version == null) continue;
                if (name.Version.CompareTo(requested.Version) < 0) continue;

                return assembly;
            }

            return null;
        }

        private static bool PublicKeyTokenEquals(AssemblyName left, AssemblyName right)
        {
            byte[] leftToken = left.GetPublicKeyToken() ?? new byte[0];
            byte[] rightToken = right.GetPublicKeyToken() ?? new byte[0];

            if (leftToken.Length != rightToken.Length) return false;

            for (int i = 0; i < leftToken.Length; i++)
            {
                if (leftToken[i] != rightToken[i]) return false;
            }

            return true;
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string bridgeDirectory = null;
            try
            {
                bridgeDirectory = Path.GetDirectoryName(typeof(BridgeBootstrap).Assembly.Location);
            }
            catch (Exception)
            {
            }

            string[] candidates =
            {
                Combine(bridgeDirectory, "BridgeDeps"),
                Combine(AppDomain.CurrentDomain.BaseDirectory, "BridgeDeps"),
                Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("lib", "BridgeDeps"))
            };

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(candidate);
                }
                catch (Exception)
                {
                    continue;
                }

                if (seen.Add(fullPath)) yield return fullPath;
            }
        }

        private static string Combine(string root, string relative)
        {
            if (string.IsNullOrEmpty(root)) return null;

            try
            {
                return Path.Combine(root, relative);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
