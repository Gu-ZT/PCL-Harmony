using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PclHarmony;

namespace PclTestPlugin
{
    /// <summary>
    /// Restores the behaviour PCL's official build compiled away when
    /// `RestrictMcDownload` (a `Public Const`) was inlined as True.
    ///
    /// Because it is a compile-time constant, every `If RestrictMcDownload`
    /// branch was folded and the FALSE half was *deleted from the IL*. There is
    /// no field to flip and no runtime check to trick: each affected call site
    /// has to have its logic rebuilt and re-attached with Harmony.
    ///
    /// Restored sites:
    ///   1. ModModpack.ModpackInstall()                      - body was replaced by a Hint
    ///   2. ModModpack.ModpackInstall(string,string,string)  - dispatch Select Case deleted
    ///   3. PageDownloadInstall.LoaderInit()                 - real client-list loader swapped
    ///                                                         for a throwing "关闭下载" task
    ///   4. PageDownloadModify.LoadInstanceFinished()        - CardMinecraft forced Collapsed
    /// </summary>
    public sealed class RestoreMcDownload : IPlugin
    {
        public string Name { get { return "PCL.Test.RestoreMcDownload"; } }

        private const string Forbidden =
            "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";

        private static Assembly _target;
        private static Harmony _harmony;

        // Site 2 plumbing: the broken method, and our drop-in replacement.
        private static MethodInfo _brokenInstall;
        private static MethodInfo _redirectInstall;

        public void Load(Harmony harmony, Assembly target)
        {
            _target = target;
            _harmony = harmony;
            PluginApi.Log("[Test] RestoreMcDownload begin (target=" + target.GetName().Version + ")");

            string sel = null;
            try { sel = Environment.GetEnvironmentVariable("PCLHARMONY_TEST_SITES"); } catch { }
            if (string.IsNullOrEmpty(sel)) sel = "1,2,3,4";
            PluginApi.Log("[Test] sites = " + sel);
            Func<int, bool> on = delegate (int n)
            {
                foreach (var s in sel.Split(','))
                    if (s.Trim() == n.ToString()) return true;
                return false;
            };

            int ok = 0;
            if (on(1) && PatchSite1_NoArgEntry()) ok++;
            if (on(2) && PatchSite2_DispatchRedirect()) ok++;
            if (on(3) && PatchSite3_RealClientListLoader()) ok++;
            if (on(4) && PatchSite4_ShowCardMinecraft()) ok++;

            var names = new List<string>();
            foreach (var m in harmony.GetPatchedMethods())
                names.Add((m.DeclaringType == null ? "?" : m.DeclaringType.FullName) + "." + m.Name);
            PluginApi.Log("[Test] done: " + ok + " site(s) restored; patched methods = "
                          + (names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray())));
        }

        // =================================================================
        // shared reflection helpers
        // =================================================================
        private static Type T(string full)
        {
            var t = _target.GetType(full, false);
            if (t == null) PluginApi.Log("[Test] !! type missing: " + full);
            return t;
        }

        private static MethodInfo M(Type t, string name, BindingFlags flags, Type[] args)
        {
            if (t == null) return null;
            try
            {
                var mi = args == null ? t.GetMethod(name, flags)
                                      : t.GetMethod(name, flags, null, args, null);
                if (mi == null) PluginApi.Log("[Test] !! method missing: " + t.FullName + "." + name);
                return mi;
            }
            catch (AmbiguousMatchException)
            {
                foreach (var m in t.GetMethods(flags))
                {
                    if (m.Name != name) continue;
                    var ps = m.GetParameters();
                    if (args == null || ps.Length != args.Length) continue;
                    bool same = true;
                    for (int i = 0; i < ps.Length; i++)
                        if (ps[i].ParameterType != args[i]) { same = false; break; }
                    if (same) return m;
                }
                PluginApi.Log("[Test] !! ambiguous: " + t.FullName + "." + name);
                return null;
            }
        }

        private static MethodInfo P(string name)
        {
            var mi = typeof(RestoreMcDownload).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (mi == null) PluginApi.Log("[Test] !! patch method missing: " + name);
            return mi;
        }

        private static bool Apply(MethodInfo original, HarmonyMethod pre, HarmonyMethod post,
                                  HarmonyMethod trans, string label)
        {
            if (original == null) { PluginApi.Log("[Test] SKIP " + label); return false; }
            try
            {
                _harmony.Patch(original, prefix: pre, postfix: post, transpiler: trans);
                PluginApi.Log("[Test] OK   " + label);
                return true;
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] FAIL " + label + ": " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // =================================================================
        // SITE 1 - ModModpack.ModpackInstall()  (parameterless entry)
        // Official IL is just `Hint(Forbidden)`. Original source prompts for a
        // pack file and runs the real install on a background thread.
        // =================================================================
        private static bool PatchSite1_NoArgEntry()
        {
            var t = T("PCL.ModModpack");
            var mi = M(t, "ModpackInstall", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
            return Apply(mi, new HarmonyMethod(P("Site1_Prefix")), null, null,
                         "site1 ModModpack.ModpackInstall()");
        }

        private static bool Site1_Prefix()
        {
            try
            {
                string file = SelectModpackFile();
                PluginApi.Log("[Test] site1 picked file: " + (file ?? "(cancelled)"));
                if (string.IsNullOrEmpty(file)) { Hint(Forbidden, true); return false; }

                var t = _target.GetType("PCL.ModModpack", true);
                var install = t.GetMethod("ModpackInstall",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(string) }, null);

                Action work = delegate
                {
                    try
                    {
                        // Goes through the redirected call site (see site 2).
                        install.Invoke(null, new object[] { file, null, null });
                    }
                    catch (TargetInvocationException tie)
                    {
                        PluginApi.Log("[Test] site1 install error: "
                                      + (tie.InnerException ?? tie).Message);
                    }
                };

                var run = FindMethod(t.BaseType ?? t, "ModBase", "RunInNewThread");
                if (run != null) run.Invoke(null, new object[] { work, "ModpackInstall", null });
                else work();
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] site1 prefix error: " + ex.GetType().Name + ": " + ex.Message);
            }
            return false;
        }

        // =================================================================
        // SITE 2 - ModModpack.ModpackInstall(string,string,string)
        //
        // This method CANNOT be patched directly: all of its normal `Return`
        // paths were folded away (the method only ever throws), so its IL ends
        // with `endfinally` and contains no `ret`. Harmony's / MonoMod's IL
        // copiers walk past the last instruction and throw
        // InvalidProgramException / ArgumentOutOfRangeException.
        //
        // Workaround: leave the original alone and redirect every *call site*
        // to a drop-in replacement with the identical signature. The callers
        // are ordinary methods, so they patch fine.
        // =================================================================
        private static bool PatchSite2_DispatchRedirect()
        {
            var t = T("PCL.ModModpack");
            _brokenInstall = M(t, "ModpackInstall", BindingFlags.Public | BindingFlags.Static,
                               new[] { typeof(string), typeof(string), typeof(string) });
            if (_brokenInstall == null) { PluginApi.Log("[Test] SKIP site2"); return false; }

            // Drop-in replacement with the exact declared return type, so the
            // rewritten `call` leaves the stack exactly as the original did.
            Type retType = _brokenInstall.ReturnType;
            var dm = new DynamicMethod("PCL_ModpackInstall_Redirect",
                retType, new[] { typeof(string), typeof(string), typeof(string) },
                typeof(RestoreMcDownload).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, P("DispatchCore"));
            if (retType.IsValueType) il.Emit(OpCodes.Unbox_Any, retType);
            else il.Emit(OpCodes.Castclass, retType);
            il.Emit(OpCodes.Ret);
            _redirectInstall = dm;

            int redirects = 0;
            var seen = new HashSet<MethodBase>();
            foreach (var type in SafeTypes())
            {
                MethodBase[] methods;
                try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (var m in methods)
                {
                    if (m == null || m.ContainsGenericParameters) continue;
                    if (!seen.Add(m)) continue;
                    if (!CallsMethod(m, _brokenInstall)) continue;

                    try
                    {
                        _harmony.Patch(m, transpiler: new HarmonyMethod(P("RedirectTranspiler")));
                        redirects++;
                        PluginApi.Log("[Test]   redirected call site in "
                                      + type.FullName + "." + m.Name);
                    }
                    catch (Exception ex)
                    {
                        PluginApi.Log("[Test]   !! redirect failed for " + type.FullName + "."
                                      + m.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                        if (Environment.GetEnvironmentVariable("PCLHARMONY_TEST_VERBOSE") == "1")
                            PluginApi.Log("[Test]   FULL: " + ex);
                    }
                }
            }

            if (redirects == 0) { PluginApi.Log("[Test] FAIL site2: no call sites redirected"); return false; }
            PluginApi.Log("[Test] OK   site2 dispatch dispatch (redirected " + redirects + " call site(s))");
            return true;
        }

        private static IEnumerable<Type> SafeTypes()
        {
            try { return _target.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                var list = new List<Type>();
                foreach (var t in rtle.Types) if (t != null) list.Add(t);
                return list;
            }
        }

        /// <summary>Cheap IL scan: does this method body reference the given method token?</summary>
        private static bool CallsMethod(MethodBase method, MethodInfo callee)
        {
            try
            {
                var body = method.GetMethodBody();
                if (body == null) return false;
                var bytes = body.GetILAsByteArray();
                if (bytes == null || bytes.Length < 5) return false;

                var module = method.Module;
                int token = callee.MetadataToken;

                for (int i = 1; i + 4 <= bytes.Length; i++)
                {
                    // token is stored little-endian after a call/callvirt/ldftn opcode
                    if (bytes[i] != (byte)(token & 0xFF)) continue;
                    if (bytes[i + 1] != (byte)((token >> 8) & 0xFF)) continue;
                    if (bytes[i + 2] != (byte)((token >> 16) & 0xFF)) continue;
                    if (bytes[i + 3] != (byte)((token >> 24) & 0xFF)) continue;

                    byte op = bytes[i - 1];
                    if (op != 0x28 && op != 0x6F && op != 0xFE) continue; // call / callvirt
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

        private static IEnumerable<CodeInstruction> RedirectTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ci in instructions)
            {
                var target = ci.operand as MethodInfo;
                if (target != null && target == _brokenInstall &&
                    (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt))
                {
                    // The call instruction can itself be a branch target (e.g.
                    // `brtrue.s IL_001c` ... `br.s IL_0022` where IL_0022 is the
                    // call). A fresh CodeInstruction carries no labels, so the
                    // branch would dangle and Harmony fails with
                    // "bad label content". Carry labels and exception blocks over.
                    var replacement = new CodeInstruction(OpCodes.Call, _redirectInstall);
                    replacement.labels.AddRange(ci.labels);
                    replacement.blocks.AddRange(ci.blocks);
                    PluginApi.Log("[Test]   rewrite call -> redirect"
                                  + (ci.labels.Count > 0 ? " (" + ci.labels.Count + " label(s) moved)" : ""));
                    yield return replacement;
                }
                else
                {
                    yield return ci;
                }
            }
        }

        /// <summary>
        /// Rebuilt `Select Case` from ModModpack.vb, routed to the private
        /// InstallPack* helpers (all of which survived in the official binary).
        /// Returns the LoaderCombo&lt;string&gt; the helper produced.
        /// </summary>
        private static object DispatchCore(string File, string InstanceName, string Logo)
        {
            try
            {
                var t = _target.GetType("PCL.ModModpack", true);
                PluginApi.Log("[Test] dispatch: file=" + File + " name=" + (InstanceName ?? "(auto)"));

                string baseFolder;
                int packType = DetectPackType(File, out baseFolder);
                DispatchCalls++;
                DispatchLastPackType = packType;

                string helper;
                object[] args;
                switch (packType)
                {
                    case 0: helper = "InstallPackCurseForge"; args = new object[] { File, null, baseFolder, InstanceName, Logo }; break;
                    case 1: helper = "InstallPackHMCL"; args = new object[] { File, null, baseFolder }; break;
                    case 2: helper = "InstallPackMMC"; args = new object[] { File, null, baseFolder }; break;
                    case 3: helper = "InstallPackMCBBS"; args = new object[] { File, null, baseFolder, InstanceName }; break;
                    case 4: helper = "InstallPackModrinth"; args = new object[] { File, null, baseFolder, InstanceName, Logo }; break;
                    case 9: helper = "InstallPackLauncherPack"; args = new object[] { File, null, baseFolder }; break;
                    default: helper = "InstallPackCompress"; args = new object[] { File, null }; break;
                }
                PluginApi.Log("[Test] dispatch: packType=" + packType + " -> " + helper
                              + " base='" + baseFolder + "'");
                DispatchLastHelper = helper;

                var hm = FindStatic(t, helper);
                if (hm == null) { PluginApi.Log("[Test] !! helper missing: " + helper); return null; }

                object zip = OpenZip(File);
                args[1] = zip;

                object result = null;
                try
                {
                    result = hm.Invoke(null, args);
                }
                finally
                {
                    // PCL's own code disposes the archive before returning.
                    if (zip != null) { try { ((IDisposable)zip).Dispose(); } catch { } }
                }

                PluginApi.Log("[Test] dispatch: " + helper + " -> "
                              + (result == null ? "null" : result.GetType().Name));
                return result;
            }
            catch (TargetInvocationException tie)
            {
                PluginApi.Log("[Test] dispatch error: " + (tie.InnerException ?? tie).Message);
                return null;
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] dispatch error: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static MethodInfo FindStatic(Type t, string name)
        {
            foreach (var m in t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
                if (m.Name == name) return m;
            return null;
        }

        /// <summary>Ported from ModModpack.ModpackInstall()'s detection block.</summary>
        private static int DetectPackType(string file, out string baseFolder)
        {
            baseFolder = "";
            object zip = null;
            try
            {
                zip = OpenZip(file);
                if (zip == null) return -1;
                var at = zip.GetType();

                if (ZipEntry(at, zip, "mcbbs.packmeta") != null) return 3;
                if (ZipEntry(at, zip, "mmc-pack.json") != null) return 2;
                if (ZipEntry(at, zip, "modrinth.index.json") != null) return 4;
                if (ZipEntry(at, zip, "manifest.json") != null) return 0;   // CurseForge
                if (ZipEntry(at, zip, "modpack.json") != null) return 1;
                if (ZipEntry(at, zip, "modpack.zip") != null) return 9;
                if (ZipEntry(at, zip, "modpack.mrpack") != null) return 9;

                var entries = at.GetProperty("Entries").GetValue(zip, null)
                              as System.Collections.IEnumerable;
                if (entries != null)
                {
                    foreach (var e in entries)
                    {
                        var full = e.GetType().GetProperty("FullName").GetValue(e, null) as string;
                        if (full == null) continue;
                        var parts = full.Split('/');
                        baseFolder = parts[0] + "/";
                        if (parts.Length != 2) continue;
                        switch (parts[1])
                        {
                            case "mcbbs.packmeta": return 3;
                            case "mmc-pack.json": return 2;
                            case "modrinth.index.json": return 4;
                            case "manifest.json": return 0;
                            case "modpack.json": return 1;
                            case "modpack.zip": return 9;
                            case "modpack.mrpack": return 9;
                        }
                    }
                }
                return -1;
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] detect failed: " + ex.GetType().Name + ": " + ex.Message);
                return -1;
            }
            finally
            {
                if (zip != null) { try { ((IDisposable)zip).Dispose(); } catch { } }
            }
        }

        private static object ZipEntry(Type archiveType, object archive, string name)
        {
            foreach (var m in archiveType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "GetEntry") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                return m.Invoke(archive, new object[] { name });
            }
            return null;
        }

        private static object OpenZip(string file)
        {
            try
            {
                var meloong = FindLoaded("MeloongCore") ?? Assembly.Load("MeloongCore");
                var fu = meloong.GetType("MeloongCore.FileUtils");
                if (fu == null) return null;
                foreach (var m in fu.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "OpenZip") continue;
                    var ps = m.GetParameters();
                    if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                    var argv = new object[ps.Length];
                    argv[0] = file;
                    for (int i = 1; i < ps.Length; i++) argv[i] = ps[i].DefaultValue;
                    return m.Invoke(null, argv);
                }
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] OpenZip failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        private static string SelectModpackFile()
        {
            try
            {
                var meloong = FindLoaded("MeloongCore") ?? Assembly.Load("MeloongCore");
                var dialogs = meloong.GetType("MeloongCore.Dialogs")
                              ?? meloong.GetType("MeloongCore.DialogUtils");
                if (dialogs == null) { PluginApi.Log("[Test] !! dialogs type missing"); return null; }

                foreach (var m in dialogs.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "SelectFile") continue;
                    var ps = m.GetParameters();
                    if (ps.Length < 2) continue;
                    var argv = BuildSelectFileArgs(ps);
                    if (argv == null) continue;

                    var res = m.Invoke(null, argv);
                    var seq = res as System.Collections.IEnumerable;
                    if (seq == null) return res as string;
                    foreach (var item in seq)
                    {
                        var s = item as string;
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] SelectModpackFile failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        private static object[] BuildSelectFileArgs(ParameterInfo[] ps)
        {
            var argv = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt == typeof(string)) argv[i] = (i == 0 ? "选择整合包压缩文件" : "");
                else if (pt == typeof(bool)) argv[i] = false;
                else if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var inner = pt.GetGenericArguments()[0];
                    var list = Activator.CreateInstance(pt);
                    if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                    {
                        var kv = Activator.CreateInstance(inner,
                            new object[] { new[] { "rar", "zip", "mrpack" }, "整合包文件" });
                        pt.GetMethod("Add").Invoke(list, new[] { kv });
                    }
                    argv[i] = list;
                }
                else if (ps[i].HasDefaultValue) argv[i] = ps[i].DefaultValue;
                else return null;
            }
            return argv;
        }

        // =================================================================
        // SITE 3 - PageDownloadInstall.LoaderInit()
        // Official IL wires a throwing "关闭下载" LoaderTask. Original source
        // wires ModDownload.DlClientListLoader.
        // =================================================================
        private static bool PatchSite3_RealClientListLoader()
        {
            var t = T("PCL.PageDownloadInstall");
            var mi = M(t, "LoaderInit", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            return Apply(mi, new HarmonyMethod(P("Site3_Prefix")), null, null,
                         "site3 PageDownloadInstall.LoaderInit()");
        }

        private static bool Site3_Prefix(object __instance)
        {
            try
            {
                var pageType = __instance.GetType();
                var baseType = pageType.BaseType;

                var controlInit = pageType.GetMethod("ControlInit",
                    BindingFlags.Public | BindingFlags.Instance);
                if (controlInit != null) controlInit.Invoke(__instance, null);

                object loadMinecraft = GetProp(pageType, __instance, "LoadMinecraft");
                object panLoad = GetProp(pageType, __instance, "PanLoad");
                object panAllBack = GetProp(pageType, __instance, "PanAllBack");

                var modDownload = _target.GetType("PCL.ModDownload", true);
                var fld = modDownload.GetField("DlClientListLoader",
                    BindingFlags.Public | BindingFlags.Static);
                object loader = fld == null ? null : fld.GetValue(null);
                if (loader == null) { PluginApi.Log("[Test] !! DlClientListLoader missing"); return true; }

                var onFinish = pageType.GetMethod("LoadMinecraft_OnFinish",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                Delegate finished = null;
                if (onFinish != null)
                {
                    var actionType = typeof(Action<>).MakeGenericType(loader.GetType());
                    finished = Delegate.CreateDelegate(actionType, __instance, onFinish, false);
                }

                var pli = FindPageLoaderInit(baseType, loader.GetType());
                if (pli == null) { PluginApi.Log("[Test] !! PageLoaderInit missing"); return true; }

                pli.Invoke(__instance, new object[]
                {
                    loadMinecraft, panLoad, panAllBack, null, loader, finished, null, true
                });

                // Record what we actually wired, so the loader's state can be
                // inspected from outside (this is the observable proof that the
                // page is no longer bound to the throwing "关闭下载" task).
                try
                {
                    var nameField = loader.GetType().GetField("Name",
                        BindingFlags.Public | BindingFlags.Instance);
                    string loaderName = nameField == null ? "(?)" : (nameField.GetValue(loader) as string);
                    Site3WiredLoaderName = loaderName;
                    Site3WiredLoaderType = loader.GetType().Name;
                    PluginApi.Log("[Test] site3 wired real loader: name='" + loaderName
                                  + "' type=" + Site3WiredLoaderType
                                  + " (throwing stub used \"关闭下载\")");
                }
                catch { }
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] site3 error: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            return false;
        }

        /// <summary>Observable proof recorded by site 3 (null when never invoked).</summary>
        public static string Site3WiredLoaderName;
        public static string Site3WiredLoaderType;

        // Observable state for external verification (see TestPluginProbe).
        public static int Site4Visits;
        public static string Site4LastVisibility;
        public static int DispatchCalls;
        public static int DispatchLastPackType = -1;
        public static string DispatchLastHelper;

        private static MethodInfo FindPageLoaderInit(Type baseType, Type loaderType)
        {
            if (!loaderType.IsGenericType) return null;
            var ga = loaderType.GetGenericArguments();
            if (ga.Length != 2) return null;
            if (baseType == null) return null;
            foreach (var m in baseType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "PageLoaderInit" || !m.IsGenericMethodDefinition) continue;
                if (m.GetGenericArguments().Length != 2) continue;
                try { return m.MakeGenericMethod(ga[0], ga[1]); }
                catch { }
            }
            return null;
        }

        // =================================================================
        // SITE 4 - PageDownloadModify.LoadInstanceFinished()
        // Official IL: CardMinecraft.Visibility = Collapsed (always).
        // Source:      If(RestrictMcDownload, Collapsed, Visible) -> Visible.
        // =================================================================
        private static bool PatchSite4_ShowCardMinecraft()
        {
            var t = T("PCL.PageDownloadModify");
            var mi = M(t, "LoadInstanceFinished", BindingFlags.NonPublic | BindingFlags.Instance, null);
            return Apply(mi, null, new HarmonyMethod(P("Site4_Postfix")), null,
                         "site4 PageDownloadModify.LoadInstanceFinished()");
        }

        private static void Site4_Postfix(object __instance)
        {
            try
            {
                object card = GetProp(__instance.GetType(), __instance, "CardMinecraft");
                if (card == null) { PluginApi.Log("[Test] site4: CardMinecraft is null"); return; }
                var vis = card.GetType().GetProperty("Visibility");
                vis.SetValue(card, EnumValue(vis.PropertyType, "Visible"), null);
                Site4Visits++;
                Site4LastVisibility = vis.GetValue(card, null) == null ? "(null)" : vis.GetValue(card, null).ToString();
                PluginApi.Log("[Test] site4 CardMinecraft -> Visible (visit #" + Site4Visits + ")");
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] site4 error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // =================================================================
        // misc
        // =================================================================
        private static object GetProp(Type t, object instance, string name)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
                                        | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (p != null) return p.GetValue(instance, null);
            var bt = t.BaseType;
            while (bt != null)
            {
                p = bt.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null) return p.GetValue(instance, null);
                bt = bt.BaseType;
            }
            return null;
        }

        private static object EnumValue(Type enumType, string name)
        {
            foreach (var v in Enum.GetValues(enumType))
                if (string.Equals(v.ToString(), name, StringComparison.Ordinal)) return v;
            return Enum.GetValues(enumType).GetValue(0);
        }

        private static MethodInfo FindMethod(Type type, string declaringTypeName, string name)
        {
            var t = _target.GetType(declaringTypeName, false);
            if (t == null || type == null) return null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType == typeof(Action)) return m;
            }
            return null;
        }

        private static Assembly FindLoaded(string simpleName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == simpleName) return a;
            return null;
        }

        private static void Hint(string message, bool red)
        {
            try
            {
                var t = _target.GetType("PCL.ModMain", true);
                var hintType = t.GetNestedType("HintType");
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Hint") continue;
                    var ps = m.GetParameters();
                    if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                    var argv = new object[ps.Length];
                    argv[0] = message;
                    for (int i = 1; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        if (pt == hintType) argv[i] = EnumValue(pt, red ? "Red" : "Blue");
                        else argv[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : false;
                    }
                    m.Invoke(null, argv);
                    return;
                }
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Test] Hint failed: " + ex.Message);
            }
        }
    }
}
