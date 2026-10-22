using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using HarmonyLib;
using PclHarmony.Contract;

namespace PclHarmony.Ce
{
    /// <summary>
    /// PCL-CE side loader, installed as a .NET startup hook.
    ///
    /// PCL-CE is a .NET 10 single-file apphost with no CLR header, so the
    /// AppDomainManager trick used for the official launcher cannot apply. .NET
    /// provides DOTNET_STARTUP_HOOKS instead: the host loads this assembly and
    /// calls <c>StartupHook.Initialize()</c> in the GLOBAL namespace before the
    /// launcher's Main() runs. Nothing about the launcher is modified.
    ///
    /// Three CoreCLR-specific facts drive the design here:
    ///   1. The hook runs in the Default AssemblyLoadContext, which does NOT probe
    ///      the hook's own directory for dependencies -> the resolver below.
    ///   2. The hook runs BEFORE Main, so launcher assemblies (Plain Craft
    ///      Launcher 2, PCL.Core, ...) are not loaded yet -> patches are queued
    ///      and applied as assemblies arrive.
    ///   3. Harmony ships no netstandard2.0 build and a netstandard2.0 caller
    ///      cannot drive it (MonoMod generic-constraint TypeLoadException), so
    ///      plugins only DESCRIBE patches and this host applies them with the
    ///      net10 Harmony next to it.
    /// </summary>
    public static class Host
    {
        private const string AppAssemblyName = "Plain Craft Launcher 2";
        private const string HostKind = "ce";

        private static readonly List<IDeclarativePlugin> Plugins = new List<IDeclarativePlugin>();
        private static readonly List<PendingPatch> Pending = new List<PendingPatch>();
        private static readonly object Gate = new object();

        private static string _baseDir;
        private static string _logPath;
        private static int _started;


        internal static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            // Register the resolver FIRST: everything below may need 0Harmony,
            // PclHarmony.Contract or a sibling plugin dll.
            try { AppDomain.CurrentDomain.AssemblyResolve += Resolve; } catch { }

            try
            {
                Log("ce host starting (runtime " + Environment.Version + ")");
                Log("base=" + BaseDir);

                DiscoverPlugins();
                CollectPatches();
                ApplyOrQueueAll();

                // Patches aimed at assemblies that had not loaded yet are applied
                // from here as they arrive.
                try { AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad; } catch { }

                int ok = 0;
                lock (Gate) { foreach (var p in Pending) if (p.Applied) ok++; }
                Log("ce host ready: " + Plugins.Count + " plugin(s), applied " + ok + " patch(es)");

                StartFlushTimer();
            }
            catch (Exception ex)
            {
                Log("ce host failed (launcher continues): " + Describe(ex));
            }
            finally
            {
                Flush();
            }
        }

        // ------------------------------------------------------------ discovery ---

        private static void DiscoverPlugins()
        {
            string dir = PluginsDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Log("plugins: no plugin folder at \"" + (dir ?? "<none>") + "\"");
                return;
            }

            string[] files;
            try { files = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) { Log("plugins: cannot enumerate: " + Describe(ex)); return; }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Log("plugins: scanning \"" + dir + "\" -> " + files.Length + " dll(s)");

            Type contract = typeof(IDeclarativePlugin);

            foreach (string file in files)
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    Assembly asm;
                    try { asm = Assembly.LoadFrom(file); }
                    catch (Exception ex)
                    {
                        Log("plugins: cannot load \"" + fileName + "\": " + Describe(ex));
                        continue;
                    }

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = (ex.Types ?? new Type[0]).Where(t => t != null).ToArray();
                        Log("plugins: \"" + fileName + "\" has unloadable types, continuing with "
                            + types.Length);
                    }

                    int found = 0;
                    foreach (Type type in types)
                    {
                        if (type == null || type.IsAbstract || type.IsInterface) continue;
                        if (!contract.IsAssignableFrom(type)) continue;
                        if (type.GetConstructor(Type.EmptyTypes) == null)
                        {
                            Log("plugins: skipping '" + type.FullName + "': no public parameterless ctor");
                            continue;
                        }
                        try
                        {
                            var inst = (IDeclarativePlugin)Activator.CreateInstance(type);
                            string name = SafeName(inst, type);
                            Plugins.Add(inst);
                            found++;
                            Log("plugin: '" + name + "' (" + type.FullName + ") from " + fileName);
                        }
                        catch (Exception ex)
                        {
                            Log("plugins: cannot instantiate '" + type.FullName + "': " + Describe(ex));
                        }
                    }

                    if (found == 0)
                        Log("plugins: \"" + fileName + "\" has no " + contract.Name + " implementation");
                }
                catch (Exception ex)
                {
                    Log("plugins: \"" + Path.GetFileName(file) + "\" failed: " + Describe(ex));
                }
            }
        }

        /// <summary>
        /// Host services handed to declarative plugins: the plugin cannot reference
        /// this host directly (it must also load on the official launcher), so
        /// logging comes through the contract.
        /// </summary>
        private sealed class CeHostServices : IPluginHost
        {
            public void Log(string message) { Host.Log(message); }
        }

        private static readonly IPluginHost HostServices = new CeHostServices();

        private static void CollectPatches()
        {
            string launcherVersion = "(unknown)";
            Assembly app = FindLauncherAssembly();
            if (app != null) launcherVersion = app.GetName().Version.ToString();

            foreach (var plugin in Plugins)
            {
                try
                {
                    var specs = plugin.GetPatches(HostKind, launcherVersion, HostServices);
                    if (specs == null) continue;
                    foreach (var spec in specs)
                    {
                        if (spec == null) continue;
                        Pending.Add(new PendingPatch { Plugin = SafeName(plugin, plugin.GetType()), Spec = spec });
                    }
                }
                catch (Exception ex)
                {
                    Log("patch specs from '" + SafeName(plugin, plugin.GetType()) + "' failed: " + Describe(ex));
                }
            }
            Log("collected " + Pending.Count + " patch spec(s) for launcher " + launcherVersion);
        }

        // --------------------------------------------------------------- patching ---

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            try
            {
                lock (Gate)
                {
                    if (Pending.Count == 0) return;
                    if (Pending.All(p => p.Applied || p.Failed)) return;
                }
                ApplyOrQueueAll();
            }
            catch { }
        }

        private static void ApplyOrQueueAll()
        {
            List<PendingPatch> todo;
            lock (Gate) { todo = Pending.Where(p => !p.Applied && !p.Failed).ToList(); }

            foreach (var item in todo)
            {
                try { TryApply(item); }
                catch (Exception ex)
                {
                    item.Failed = true;
                    Log("patch failed " + item.Spec + ": " + Describe(ex));
                }
            }
            Flush();
        }

        private static void TryApply(PendingPatch item)
        {
            var spec = item.Spec;
            if (spec == null) { item.Failed = true; return; }

            Type targetType = ResolveType(spec.TypeName);
            if (targetType == null) return;   // assembly not loaded yet; retried on AssemblyLoad

            MethodInfo target = FindMethod(targetType, spec.MethodName, spec.TargetParameterTypes);
            if (target == null)
            {
                item.Failed = true;
                Log("patch target not found: " + spec.TypeName + "." + spec.MethodName);
                return;
            }

            Type hookType = ResolveType(spec.HookTypeName);
            if (hookType == null)
            {
                item.Failed = true;
                Log("patch hook type not found: " + spec.HookTypeName);
                return;
            }

            MethodInfo hook = hookType.GetMethod(spec.HookMethodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (hook == null)
            {
                item.Failed = true;
                Log("patch hook method not found: " + spec.HookTypeName + "." + spec.HookMethodName);
                return;
            }

            // A compiler-generated closure cannot be handed to Harmony even though
            // the identical body works as a named static method.
            if (hook.Name.IndexOf('<') >= 0 || hook.Name.IndexOf('>') >= 0)
            {
                item.Failed = true;
                Log("patch hook is a compiler-generated closure, write it as a real static method: "
                    + spec.HookTypeName + "." + spec.HookMethodName);
                return;
            }

            var harmony = GetHarmony();
            if (string.Equals(spec.HookKind, "redirect", StringComparison.OrdinalIgnoreCase))
            {
                int redirected = RedirectCallSites(harmony, target, hook);
                if (redirected == 0)
                {
                    item.Failed = true;
                    Log("no call sites of " + spec.TypeName + "." + spec.MethodName + " to redirect");
                    return;
                }
                item.Applied = true;
                Log("applied redirect " + spec.TypeName + "." + spec.MethodName
                    + " -> " + spec.HookTypeName + "." + spec.HookMethodName
                    + " (" + redirected + " call site(s), plugin '" + item.Plugin + "')");
                return;
            }

            var hm = new HarmonyMethod(hook);
            bool prefix = string.Equals(spec.HookKind, "prefix", StringComparison.OrdinalIgnoreCase);
            if (prefix) harmony.Patch(target, hm, null, null);
            else harmony.Patch(target, null, hm, null);

            item.Applied = true;
            Log("applied " + (prefix ? "prefix" : "postfix") + " " + spec.TypeName + "." + spec.MethodName
                + " (plugin '" + item.Plugin + "')");
        }

        /// <summary>
        /// Rewrites every call to <paramref name="target"/> into a call to
        /// <paramref name="replacement"/>.
        ///
        /// Needed for targets whose IL cannot be patched (no `ret` left once the
        /// compiler folded every return away): Harmony's IL copier walks off the end.
        /// Their callers are ordinary methods and can be rewritten. The replacement
        /// takes the same arguments and returns `object`; a cast restores the declared
        /// return type so the stack shape matches the original call.
        ///
        /// Kept in step with the net48 loader so the contract behaves identically on
        /// both launchers.
        /// </summary>
        private static int RedirectCallSites(Harmony harmony, MethodInfo target, MethodInfo replacement)
        {
            MethodInfo transpilerMethod = typeof(Host).GetMethod("RedirectTranspiler",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (transpilerMethod == null) return 0;
            var transpiler = new HarmonyMethod(transpilerMethod);

            Assembly host = target.DeclaringType == null ? null : target.DeclaringType.Assembly;
            if (host == null) return 0;

            Type[] types;
            try { types = host.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = (ex.Types ?? new Type[0]).Where(t => t != null).ToArray();
            }

            int redirected = 0;
            var seen = new HashSet<MethodBase>();

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
                        _redirectTarget = target;
                        _redirectReplacement = replacement;
                        harmony.Patch(m, null, null, transpiler);
                        redirected++;
                        Log("  redirect call site in " + type.FullName + "." + m.Name);
                    }
                    catch (Exception ex)
                    {
                        Log("  redirect failed for " + type.FullName + "." + m.Name + ": " + Describe(ex));
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
                var called = ci.operand as MethodInfo;
                bool isCall = ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt;
                if (called == null || !isCall || !SameMethod(called, target))
                {
                    yield return ci;
                    continue;
                }

                // The call can itself be a branch target; a fresh CodeInstruction has
                // no labels, so carry them (and exception blocks) over or Harmony fails
                // with "bad label content".
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

        private static MethodInfo FindMethod(Type type, string name, string[] paramTypeNames)
        {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Static | BindingFlags.Instance)
                                 .Where(m => m.Name == name).ToArray();
            if (candidates.Length == 0) return null;
            // null = no constraint; an empty array means "takes no parameters".
            if (paramTypeNames == null) return candidates[0];

            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                if (ps.Length != paramTypeNames.Length) continue;
                bool match = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (!string.Equals(ps[i].ParameterType.Name, paramTypeNames[i], StringComparison.Ordinal))
                    { match = false; break; }
                }
                if (match) return m;
            }
            return candidates[0];
        }

        private static Type ResolveType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            try { return Type.GetType(fullName, false); } catch { }
            return null;
        }

        private static Assembly FindLauncherAssembly()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetName().Name == AppAssemblyName) return a; }
                catch { }
            }
            return null;
        }

        // ---------------------------------------------------------------- harmony ---

        private static Harmony _harmony;

        private static Harmony GetHarmony()
        {
            if (_harmony != null) return _harmony;
            _harmony = new Harmony("PclHarmony.Ce");
            return _harmony;
        }

        // -------------------------------------------------------------- resolution ---

        /// <summary>
        /// Resolves dependencies from the launcher directory, our own directory and
        /// plugins\. Without this the CoreCLR hook cannot find 0Harmony at all.
        /// </summary>
        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string requested;
                try { requested = new AssemblyName(args.Name).Name; }
                catch { return null; }

                foreach (string dir in new[] { BaseDir, PluginsDir, OwnDir })
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string candidate = Path.Combine(dir, requested + ".dll");
                    if (File.Exists(candidate))
                    {
                        var asm = Assembly.LoadFrom(candidate);
                        Log("resolver: served " + requested + " from " + candidate);
                        return asm;
                    }
                }
            }
            catch (Exception ex) { Log("resolver: failed for " + args.Name + ": " + Describe(ex)); }
            return null;
        }

        private static string OwnDir
        {
            get
            {
                try
                {
                    var asm = typeof(Host).Assembly;
                    if (!string.IsNullOrEmpty(asm.Location)) return Path.GetDirectoryName(asm.Location);
                }
                catch { }
                return null;
            }
        }

        internal static string BaseDir
        {
            get
            {
                if (_baseDir != null) return _baseDir;
                string dir = OwnDir;
                if (string.IsNullOrEmpty(dir))
                {
                    try { dir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
                }
                if (string.IsNullOrEmpty(dir))
                {
                    try { dir = Path.GetTempPath(); } catch { dir = "."; }
                }
                _baseDir = dir;
                return dir;
            }
        }

        internal static string PluginsDir
        {
            get
            {
                string dir = BaseDir;
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "plugins");
            }
        }

        // -------------------------------------------------------------------- log ---

        private static readonly object LogGate = new object();
        private static readonly StringBuilder Buffer = new StringBuilder();
        private static Timer _timer;

        internal static string LogPath
        {
            get
            {
                if (_logPath != null) return _logPath;
                string dir = BaseDir;
                _logPath = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "PclHarmony.log");
                return _logPath;
            }
        }

        internal static void Log(string message)
        {
            if (message == null) return;
            try
            {
                lock (LogGate)
                {
                    Buffer.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                          .Append(" [T").Append(Thread.CurrentThread.ManagedThreadId.ToString("00")).Append("] ")
                          .Append(message).Append(Environment.NewLine);
                    if (Buffer.Length > 8192) FlushLocked();
                }
            }
            catch { }
        }

        internal static void Flush()
        {
            lock (LogGate) { FlushLocked(); }
        }

        private static void FlushLocked()
        {
            if (Buffer.Length == 0) return;
            string text = Buffer.ToString();
            Buffer.Length = 0;
            try
            {
                using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                }
            }
            catch { }
        }

        private static void StartFlushTimer()
        {
            try
            {
                _timer = new Timer(delegate { Flush(); }, null, 2000, 2000);
                AppDomain.CurrentDomain.ProcessExit += delegate { Flush(); };
            }
            catch { }
        }

        // ---------------------------------------------------------------- helpers ---

        private static string SafeName(IDeclarativePlugin plugin, Type type)
        {
            try
            {
                string n = plugin.Name;
                return string.IsNullOrEmpty(n) ? type.Name : n;
            }
            catch { return type.Name; }
        }

        internal static string Describe(Exception ex)
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

        private sealed class PendingPatch
        {
            public string Plugin;
            public PatchSpec Spec;
            public bool Applied;
            public bool Failed;
        }
    }
}
