using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using PclHarmony.Contract;

namespace PclHarmony
{
    /// <summary>
    /// Discovers and loads plugins from &lt;exe dir&gt;\plugins\*.dll.
    /// A broken plugin must never stop the launcher: every step is isolated.
    /// </summary>
    internal static class PluginHost
    {
        internal const string PluginsFolderName = "plugins";

        /// <summary>
        /// Runtime-neutral plugin contract (netstandard2.0), see Contract.cs.
        ///
        /// Resolved lazily on purpose: a static field initialiser runs while the
        /// PluginHost type itself is being initialised, which is before the
        /// AppDomainManager bootstrap has finished resolving our own dependencies.
        /// Touching typeof(IDeclarativePlugin) there throws TypeInitializationException
        /// and takes the whole loader down with it.
        /// </summary>
        private static Type DeclarativeInterface
        {
            get
            {
                if (_declarativeInterface != null) return _declarativeInterface;
                try { _declarativeInterface = Type.GetType("PclHarmony.Contract.IDeclarativePlugin, PclHarmony.Contract", false); }
                catch { }
                if (_declarativeInterface == null)
                {
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            Type t = asm.GetType("PclHarmony.Contract.IDeclarativePlugin", false);
                            if (t != null) { _declarativeInterface = t; break; }
                        }
                        catch { }
                    }
                }
                return _declarativeInterface;
            }
        }

        private static Type _declarativeInterface;
        private const int MaxDepth = 2;

        private static readonly List<string> Loaded = new List<string>();
        private static Assembly _target;
        private static int _started;

        internal static string PluginsDirectory
        {
            get
            {
                string baseDir = PluginApi.GetExecutableDirectory();
                if (string.IsNullOrEmpty(baseDir)) return null;
                return Path.Combine(baseDir, PluginsFolderName);
            }
        }

        internal static IList<string> LoadedPlugins
        {
            get { lock (Loaded) { return Loaded.ToArray(); } }
        }

        internal static void Start(Assembly target)
        {
            if (System.Threading.Interlocked.Exchange(ref _started, 1) != 0) return;
            _target = target;
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += OnResolvePluginDependency;
            }
            catch { }

            try { Scan(); }
            catch (Exception ex)
            {
                LoaderLog.Info("plugin scan aborted: " + Describe(ex));
            }
            LoaderLog.Flush();
        }

        private static void Scan()
        {
            string dir = PluginsDirectory;
            LoaderLog.Info("loader initialised: base=\"" + PluginApi.GetExecutableDirectory()
                           + "\", plugins=\"" + (dir ?? "<none>") + "\"");

            if (string.IsNullOrEmpty(dir))
            {
                LoaderLog.Info("plugins: no plugin directory (no base directory), skipping");
                return;
            }

            if (!Directory.Exists(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    LoaderLog.Info("plugins: created \"" + dir + "\"");
                }
                catch (Exception ex)
                {
                    LoaderLog.Info("plugins: cannot create \"" + dir + "\": " + Describe(ex));
                }
            }

            if (!Directory.Exists(dir))
            {
                LoaderLog.Info("plugins: directory missing, 0 plugins loaded");
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                LoaderLog.Info("plugins: cannot enumerate \"" + dir + "\": " + Describe(ex));
                return;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            LoaderLog.Info("plugins: scanning \"" + dir + "\" -> " + files.Length + " dll(s)");

            int loadedCount = 0;
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    loadedCount += LoadFile(files[i]);
                }
                catch (Exception ex)
                {
                    LoaderLog.Info("plugins: \"" + Path.GetFileName(files[i]) + "\" failed: " + Describe(ex));
                }
            }

            LoaderLog.Info("plugins: " + loadedCount + " plugin(s) loaded from " + files.Length + " dll(s)");
        }

        private static int LoadFile(string path)
        {
            string fileName = Path.GetFileName(path);
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (Exception ex)
            {
                LoaderLog.Info("plugins: cannot load \"" + fileName + "\": " + Describe(ex));
                return 0;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = Flatten(ex);
                LoaderLog.Info("plugins: \"" + fileName + "\" has unloadable types, continuing with "
                               + types.Length + " (" + Describe(ex) + ")");
            }
            catch (Exception ex)
            {
                LoaderLog.Info("plugins: cannot reflect \"" + fileName + "\": " + Describe(ex));
                return 0;
            }

            Type pluginInterface = typeof(IPlugin);
            int count = 0;

            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null || type.IsAbstract || type.IsInterface) continue;

                // Declarative plugins first: they are the runtime-neutral kind, and a
                // type could in principle implement both interfaces.
                if (DeclarativeInterface.IsAssignableFrom(type))
                {
                    count += LoadDeclarative(type, fileName);
                    continue;
                }

                if (!pluginInterface.IsAssignableFrom(type))
                {
                    // Assembly-name mismatch (plugin compiled against a different
                    // PclHarmony copy) also shows up here; report it once per dll.
                    if (LooksLikePlugin(type))
                        LoaderLog.Info("plugins: skipping '" + type.FullName + "' in \"" + fileName
                                       + "\": does not implement " + pluginInterface.AssemblyQualifiedName);
                    continue;
                }
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    LoaderLog.Info("plugins: skipping '" + type.FullName + "': no public parameterless constructor");
                    continue;
                }

                IPlugin instance;
                try
                {
                    instance = (IPlugin)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    LoaderLog.Info("plugins: cannot instantiate '" + type.FullName + "': " + Describe(ex));
                    continue;
                }

                string name = SafeName(instance, type);
                Harmony harmony = PluginApi.GetHarmony(name);
                LoaderLog.Info("plugin: loading '" + name + "' (" + type.FullName + ") from " + fileName);

                try
                {
                    instance.Load(harmony, _target);
                    lock (Loaded) { Loaded.Add(name); }
                    count++;
                    int patchCount = 0;
                    try
                    {
                        foreach (MethodBase ignored in harmony.GetPatchedMethods()) patchCount++;
                    }
                    catch { }
                    LoaderLog.Info("plugin: loaded '" + name + "' OK, harmony=\"" + name + "\", patch count="
                                   + patchCount);
                }
                catch (Exception ex)
                {
                    // Undo whatever half-applied patches this plugin managed.
                    try { harmony.UnpatchAll(harmony.Id); }
                    catch (Exception undo)
                    {
                        LoaderLog.Info("plugin: '" + name + "' unpatch after failure also failed: " + Describe(undo));
                    }
                    LoaderLog.Info("plugin: '" + name + "' FAILED to load (isolated, launcher continues): " + Describe(ex));
                }
            }

            if (count == 0 && !LooksLikePluginAssembly(types))
                LoaderLog.Info("plugins: \"" + fileName + "\" contains no " + pluginInterface.Name + " implementation");

            return count;
        }

        /// <summary>
        /// Loads a runtime-neutral, declarative plugin: the type only DESCRIBES the
        /// patches it wants and this loader applies them with its own net48 Harmony.
        ///
        /// The indirection exists because Lib.Harmony has no netstandard2.0 build, so
        /// a plugin that must run on both CLR 4 and CoreCLR cannot mention Harmony in
        /// its own code. See PclHarmony.Contract.IDeclarativePlugin.
        /// </summary>
        private static int LoadDeclarative(Type type, string fileName)
        {
            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                LoaderLog.Info("plugins: skipping '" + type.FullName + "': no public parameterless constructor");
                return 0;
            }

            IDeclarativePlugin plugin;
            try
            {
                plugin = (IDeclarativePlugin)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                LoaderLog.Info("plugins: cannot instantiate '" + type.FullName + "': " + Describe(ex));
                return 0;
            }

            string name = SafeName(plugin, type);
            Harmony harmony = PluginApi.GetHarmony(name);
            LoaderLog.Info("plugin: loading declarative '" + name + "' (" + type.FullName + ") from " + fileName);

            int applied = 0;
            try
            {
                string version = _target == null ? "(none)" : _target.GetName().Version.ToString();
                var specs = plugin.GetPatches("official", version);
                if (specs != null)
                {
                    foreach (PatchSpec spec in specs)
                    {
                        if (spec == null) continue;
                        try
                        {
                            ApplyDeclarativePatch(harmony, spec);
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            LoaderLog.Info("plugin: '" + name + "' patch failed " + spec + ": " + Describe(ex));
                        }
                    }
                }

                lock (Loaded) { Loaded.Add(name); }
                LoaderLog.Info("plugin: loaded declarative '" + name + "' OK, harmony=\"" + name
                               + "\", patch count=" + applied);
                return 1;
            }
            catch (Exception ex)
            {
                try { harmony.UnpatchAll(harmony.Id); } catch { }
                LoaderLog.Info("plugin: declarative '" + name + "' FAILED (isolated, launcher continues): "
                               + Describe(ex));
                return 0;
            }
        }

        private static void ApplyDeclarativePatch(Harmony harmony, PatchSpec spec)
        {
            Type targetType = FindType(spec.TypeName);
            if (targetType == null) throw new InvalidOperationException("target type not found: " + spec.TypeName);

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Static | BindingFlags.Instance;
            MethodInfo target = null;
            foreach (MethodInfo m in targetType.GetMethods(flags))
            {
                if (m.Name != spec.MethodName) continue;
                if (!ParametersMatch(m, spec.TargetParameterTypes)) continue;
                target = m;
                break;
            }
            if (target == null)
                throw new InvalidOperationException("target method not found: " + spec.TypeName + "." + spec.MethodName);

            Type hookType = FindType(spec.HookTypeName);
            if (hookType == null) throw new InvalidOperationException("hook type not found: " + spec.HookTypeName);

            MethodInfo hook = hookType.GetMethod(spec.HookMethodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (hook == null)
                throw new InvalidOperationException("hook method not found: "
                                                    + spec.HookTypeName + "." + spec.HookMethodName);

            // A compiler-generated closure cannot be handed to Harmony even though an
            // identical named static method works.
            if (hook.Name.IndexOf('<') >= 0)
                throw new InvalidOperationException("hook is a compiler-generated closure; "
                    + "write it as a real static method: " + spec.HookTypeName + "." + spec.HookMethodName);

            HarmonyMethod hm = new HarmonyMethod(hook);
            if (string.Equals(spec.HookKind, "prefix", StringComparison.OrdinalIgnoreCase))
                harmony.Patch(target, hm, null, null);
            else
                harmony.Patch(target, null, hm, null);

            LoaderLog.Info("plugin: applied " + spec.HookKind + " " + spec.TypeName + "." + spec.MethodName);
        }

        private static bool ParametersMatch(MethodInfo m, string[] typeNames)
        {
            if (typeNames == null || typeNames.Length == 0) return true;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length != typeNames.Length) return false;
            for (int i = 0; i < ps.Length; i++)
                if (!string.Equals(ps[i].ParameterType.Name, typeNames[i], StringComparison.Ordinal))
                    return false;
            return true;
        }

        private static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            try { return Type.GetType(fullName, false); } catch { }
            return null;
        }

        private static Type[] Flatten(ReflectionTypeLoadException ex)
        {
            Type[] raw = ex.Types;
            if (raw == null) return new Type[0];
            int n = 0;
            for (int i = 0; i < raw.Length; i++) if (raw[i] != null) n++;
            Type[] clean = new Type[n];
            int j = 0;
            for (int i = 0; i < raw.Length; i++) if (raw[i] != null) clean[j++] = raw[i];
            return clean;
        }

        private static string SafeName(IDeclarativePlugin instance, Type type)
        {
            try
            {
                string name = instance.Name;
                if (string.IsNullOrEmpty(name)) return type.Name;
                return name;
            }
            catch (Exception ex)
            {
                LoaderLog.Info("plugin: '" + type.FullName + "'.Name threw, using type name: " + Describe(ex));
                return type.Name;
            }
        }

        private static string SafeName(IPlugin instance, Type type)
        {
            try
            {
                string name = instance.Name;
                if (string.IsNullOrEmpty(name)) return type.Name;
                return name;
            }
            catch (Exception ex)
            {
                LoaderLog.Info("plugin: '" + type.FullName + "'.Name threw, using type name: " + Describe(ex));
                return type.Name;
            }
        }

        /// <summary>Heuristic: a type whose name is "…Plugin" but doesn't implement our IPlugin.</summary>
        private static bool LooksLikePlugin(Type type)
        {
            try
            {
                string n = type.Name;
                return n != null && n.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase)
                       || (n != null && n.EndsWith("Plugin", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private static bool LooksLikePluginAssembly(Type[] types)
        {
            for (int i = 0; i < types.Length; i++)
                if (types[i] != null && LooksLikePlugin(types[i])) return true;
            return false;
        }

        private static string _ownName;
        private static string _harmonyName = "0Harmony";

        /// <summary>
        /// Lets plugins resolve PclHarmony / 0Harmony / sibling plugin DLLs that live
        /// next to the launcher exe or in the plugins folder.
        /// </summary>
        private static Assembly OnResolvePluginDependency(object sender, ResolveEventArgs args)
        {
            try
            {
                string requested;
                try { requested = new AssemblyName(args.Name).Name; }
                catch { return null; }

                if (_ownName == null) _ownName = typeof(PluginApi).Assembly.GetName().Name;

                // Never re-serve ourselves or Harmony with a second copy: identity
                // must stay identical to what the loader and plugins were built against.
                if (string.Equals(requested, _ownName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(requested, _harmonyName, StringComparison.OrdinalIgnoreCase))
                {
                    Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (string.Equals(all[i].GetName().Name, requested, StringComparison.OrdinalIgnoreCase))
                            return all[i];
                    }
                    return null;
                }

                string baseDir = PluginApi.GetExecutableDirectory();
                string plugins = PluginsDirectory;

                string candidate = null;
                if (!string.IsNullOrEmpty(baseDir)) candidate = Path.Combine(baseDir, requested + ".dll");
                if ((candidate == null || !File.Exists(candidate)) && !string.IsNullOrEmpty(plugins))
                {
                    string inPlugins = Path.Combine(plugins, requested + ".dll");
                    if (File.Exists(inPlugins)) candidate = inPlugins;
                }
                if (candidate == null || !File.Exists(candidate)) return null;

                Assembly loaded = Assembly.LoadFrom(candidate);
                LoaderLog.Info("plugins: resolved dependency " + requested + " from " + candidate);
                return loaded;
            }
            catch (Exception ex)
            {
                try { LoaderLog.Info("plugins: dependency resolve failed for " + args.Name + ": " + Describe(ex)); }
                catch { }
                return null;
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex == null) return "<null>";
            try
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException : ex;
                string text = inner.GetType().Name + ": " + inner.Message;
                if (inner.InnerException != null && !ReferenceEquals(inner.InnerException, inner))
                    text += " -> " + inner.InnerException.GetType().Name + ": " + inner.InnerException.Message;
                return text.Replace("\r", " ").Replace("\n", " ");
            }
            catch { return ex.GetType().Name; }
        }
    }
}
