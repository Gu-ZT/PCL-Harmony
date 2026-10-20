using System;
using System.Collections.Generic;
using System.Reflection;
using System.Resources;

namespace PclHarmony
{
    /// <summary>
    /// Serves the launcher's embedded dependency assemblies out of the manifest
    /// resource "PCL.Resources.resources".
    ///
    /// PCL embeds NAudio, Newtonsoft.Json, ... as byte[] inside that resource and
    /// resolves them in an AssemblyResolve handler registered in Application.New().
    /// We must serve them EARLIER, because Harmony needs to resolve metadata of
    /// patched types before Application.New() has ever run.
    /// </summary>
    internal static class EmbeddedResolver
    {
        private const string ResourceName = "PCL.Resources.resources";

        // Longest prefixes first is not required: we do exact-or-prefix matching on
        // the requested simple name and return the matching resource key.
        private static readonly string[] Prefixes =
        {
            "NAudio",
            "Newtonsoft.Json",
            "Ookii.Dialogs.Wpf",
            "Imazen.WebP",
            "CacheCow.Common",
            "CacheCow.Client.FileStore",
            "CacheCow.Client",
            "ThrottleDebounce",
            "System.Net.Http.Formatting",
            "Microsoft.Win32.Registry",
            "PCLCS",
            "MeloongCore.Wpf",
            "MeloongCore"
        };

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Assembly> Cache =
            new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private static Assembly _target;
        private static bool _installed;

        internal static void Install(Assembly target)
        {
            lock (Gate)
            {
                if (_installed) return;
                _target = target;
                _installed = true;
            }
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            LoaderLog.Info("embedded assembly resolver installed (resource \"" + ResourceName + "\", "
                           + Prefixes.Length + " prefixes)");
        }

        private static string MatchPrefix(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;
            for (int i = 0; i < Prefixes.Length; i++)
            {
                string p = Prefixes[i];
                if (simpleName == p) return p;
                if (simpleName.Length > p.Length
                    && simpleName.StartsWith(p, StringComparison.Ordinal))
                    return p;
            }
            return null;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string requested;
                try { requested = new AssemblyName(args.Name).Name; }
                catch { return null; }

                string prefix = MatchPrefix(requested);
                if (prefix == null) return null;

                lock (Gate)
                {
                    Assembly cached;
                    if (Cache.TryGetValue(prefix, out cached)) return cached;

                    Assembly launcher = _target ?? Assembly.GetEntryAssembly();
                    if (launcher == null) return null;

                    using (System.IO.Stream stream = launcher.GetManifestResourceStream(ResourceName))
                    {
                        if (stream == null)
                        {
                            LoaderLog.Info("resolver: resource " + ResourceName + " not found in "
                                           + launcher.GetName().Name);
                            return null;
                        }
                        using (ResourceReader reader = new ResourceReader(stream))
                        {
                            foreach (System.Collections.DictionaryEntry entry in reader)
                            {
                                string key = entry.Key as string;
                                if (key == null) continue;
                                if (!string.Equals(key, requested, StringComparison.Ordinal)
                                    && !string.Equals(key, prefix, StringComparison.Ordinal))
                                    continue;

                                byte[] bytes = entry.Value as byte[];
                                if (bytes == null) continue;
                                Assembly loaded = Assembly.Load(bytes);
                                Cache[prefix] = loaded;
                                Cache[requested] = loaded;
                                LoaderLog.Info("resolver: served " + requested + " ("
                                               + bytes.Length + " bytes) for " + args.Name);
                                return loaded;
                            }
                        }
                    }
                    LoaderLog.Info("resolver: no embedded entry for " + requested + " (wanted by "
                                   + (args.RequestingAssembly == null ? "?" : args.RequestingAssembly.GetName().Name) + ")");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    LoaderLog.Info("resolver: failed for " + args.Name + ": "
                                   + ex.GetType().Name + ": " + ex.Message);
                }
                catch { }
            }
            return null;
        }
    }
}
