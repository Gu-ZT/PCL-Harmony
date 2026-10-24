using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        /// Host services handed to declarative plugins. The plugin cannot reference
        /// this loader directly (it must also load on PCL-CE), so logging comes
        /// through the contract.
        /// </summary>
        private sealed class DeclarativeHostServices : IPluginHost
        {
            public void Log(string message) { LoaderLog.Info(message); }
        }

        private static readonly IPluginHost HostServices = new DeclarativeHostServices();

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
                var specs = plugin.GetPatches("official", version, HostServices);
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
            MethodBase target = null;
            foreach (MethodInfo m in targetType.GetMethods(flags))
            {
                if (m.Name != spec.MethodName) continue;
                if (!ParametersMatch(m, spec.TargetParameterTypes)) continue;
                target = m;
                break;
            }

            // GetMethods does not return constructors, but a plugin may need one:
            // work a launcher does inside a constructor is not reachable otherwise.
            if (target == null && (spec.MethodName == ".ctor" || spec.MethodName == ".cctor"))
            {
                ConstructorInfo ctor = null;
                foreach (ConstructorInfo c in targetType.GetConstructors(
                             BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (c.IsStatic != (spec.MethodName == ".cctor")) continue;
                    if (!ParametersMatch(c, spec.TargetParameterTypes)) continue;
                    ctor = c;
                    break;
                }
                if (ctor != null) target = ctor;
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

            if (string.Equals(spec.HookKind, "redirect", StringComparison.OrdinalIgnoreCase))
            {
                // Call-site rewriting works on IL, so the target has to be a method;
                // a constructor cannot be redirected.
                var redirectTarget = target as MethodInfo;
                if (redirectTarget == null)
                    throw new InvalidOperationException("redirect requires a method, not a constructor: "
                                                        + spec.TypeName + "." + spec.MethodName);

                int redirected = RedirectCallSites(harmony, redirectTarget, hook);
                if (redirected == 0)
                    throw new InvalidOperationException("no call sites of " + spec.TypeName + "."
                                                        + spec.MethodName + " were found to redirect");
                LoaderLog.Info("plugin: applied redirect " + spec.TypeName + "." + spec.MethodName
                               + " -> " + spec.HookTypeName + "." + spec.HookMethodName
                               + " (" + redirected + " call site(s))");
                return;
            }

            HarmonyMethod hm = new HarmonyMethod(hook);
            if (string.Equals(spec.HookKind, "prefix", StringComparison.OrdinalIgnoreCase))
                harmony.Patch(target, hm, null, null);
            else
                harmony.Patch(target, null, hm, null);

            LoaderLog.Info("plugin: applied " + spec.HookKind + " " + spec.TypeName + "." + spec.MethodName);
        }

        /// <summary>
        /// Rewrites every call to <paramref name="target"/> into a call to
        /// <paramref name="replacement"/>, for targets that cannot be patched directly.
        ///
        /// A method whose IL ends without `ret` (all normal returns folded away) makes
        /// Harmony's IL copier walk off the end: InvalidProgramException /
        /// "Offset N outside valid range". Such a method cannot be patched, but its
        /// CALLERS are ordinary and can. The replacement must accept the same
        /// arguments and return `object`; the declared return type is restored with a
        /// cast so the rewritten stack shape matches the original call exactly.
        /// </summary>
        private static int RedirectCallSites(Harmony harmony, MethodInfo target, MethodInfo replacement)
        {
            var transpiler = new HarmonyMethod(
                typeof(PluginHost).GetMethod("RedirectTranspiler",
                    BindingFlags.NonPublic | BindingFlags.Static));

            int redirected = 0;
            var seen = new HashSet<MethodBase>();

            Assembly host = target.DeclaringType == null ? null : target.DeclaringType.Assembly;
            if (host == null) return 0;

            Type[] types;
            try { types = host.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = (ex.Types ?? new Type[0]).Where(t => t != null).ToArray();
            }

            foreach (Type type in types)
            {
                MethodBase[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                              | BindingFlags.Static | BindingFlags.Instance
                                              | BindingFlags.DeclaredOnly);
                }
                catch { continue; }

                foreach (MethodBase m in methods)
                {
                    if (m == null || m.ContainsGenericParameters) continue;
                    if (ReferenceEquals(m, target)) continue;
                    if (!seen.Add(m)) continue;
                    if (!CallsMethod(m, target)) continue;

                    try
                    {
                        // Read by the transpiler while this Patch call runs.
                        _redirectTarget = target;
                        _redirectReplacement = replacement;
                        harmony.Patch(m, null, null, transpiler);
                        redirected++;
                        LoaderLog.Info("plugin:   redirect call site in " + type.FullName + "." + m.Name);
                    }
                    catch (Exception ex)
                    {
                        LoaderLog.Info("plugin:   redirect failed for " + type.FullName + "." + m.Name
                                       + ": " + Describe(ex));
                    }
                }
            }
            return redirected;
        }

        private static MethodInfo _redirectTarget;
        private static MethodInfo _redirectReplacement;

        private static IEnumerable<CodeInstruction> RedirectTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo target = _redirectTarget;
            MethodInfo replacement = _redirectReplacement;
            Type returnType = target == null ? typeof(void) : target.ReturnType;

            foreach (CodeInstruction ci in instructions)
            {
                MethodInfo called = ci.operand as MethodInfo;
                bool isCall = ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt;
                if (called == null || !isCall || !SameMethod(called, target))
                {
                    yield return ci;
                    continue;
                }

                // The call instruction can itself be a branch target. A fresh
                // CodeInstruction carries no labels, so the branch would dangle and
                // Harmony fails with "bad label content" - carry labels and blocks over.
                var rewritten = new CodeInstruction(OpCodes.Call, replacement);
                rewritten.labels.AddRange(ci.labels);
                rewritten.blocks.AddRange(ci.blocks);
                yield return rewritten;

                if (returnType != typeof(void) && returnType != typeof(object))
                {
                    yield return returnType.IsValueType
                        ? new CodeInstruction(OpCodes.Unbox_Any, returnType)
                        : new CodeInstruction(OpCodes.Castclass, returnType);
                }
            }
        }

        private static bool SameMethod(MethodInfo a, MethodInfo b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.MetadataToken == b.MetadataToken && ReferenceEquals(a.Module, b.Module);
        }

        /// <summary>Cheap IL scan: does this method body reference the given method token?</summary>
        private static bool CallsMethod(MethodBase method, MethodInfo callee)
        {
            try
            {
                MethodBody body = method.GetMethodBody();
                if (body == null) return false;
                byte[] bytes = body.GetILAsByteArray();
                if (bytes == null || bytes.Length < 5) return false;

                Module module = method.Module;
                int token = callee.MetadataToken;

                for (int i = 1; i + 4 <= bytes.Length; i++)
                {
                    if (bytes[i] != (byte)(token & 0xFF)) continue;
                    if (bytes[i + 1] != (byte)((token >> 8) & 0xFF)) continue;
                    if (bytes[i + 2] != (byte)((token >> 16) & 0xFF)) continue;
                    if (bytes[i + 3] != (byte)((token >> 24) & 0xFF)) continue;

                    byte op = bytes[i - 1];
                    if (op != 0x28 && op != 0x6F) continue;   // call / callvirt
                    try
                    {
                        if (module.ResolveMethod(token) == callee) return true;
                    }
                    catch { return true; }
                }
            }
            catch { }
            return false;
        }

        private static bool ParametersMatch(MethodBase m, string[] typeNames)
        {
            if (typeNames == null) return true;   // null = no constraint; empty = zero parameters
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
