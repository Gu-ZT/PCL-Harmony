using System;
using System.Collections.Generic;
using System.Reflection;
using PclHarmony.Contract;

namespace PclExamplePlugin
{
    /// <summary>
    /// Reference implementation of a plugin.
    ///
    /// It exists to show the shape of a plugin and the mechanics that matter when
    /// writing one against a real launcher build: resolving types from a foreign
    /// assembly, attaching prefixed/transpiled patches, keeping every failure
    /// isolated, and reporting through the loader log. The features it happens to
    /// demonstrate are incidental - treat this class as a template to copy from,
    /// not as a description of what a plugin must do.
    ///
    /// The internals are grouped by "site": an independent, individually
    /// switchable unit of work. Sites are a convenience for experimenting with
    /// one patch at a time rather than a model to follow.
    /// </summary>
    public sealed class ExamplePlugin : IDeclarativePlugin
    {
        public string Name { get { return "PCL.Example.ExamplePlugin"; } }

        private const string LegacyNotice = "***";

        private static Assembly _target;
        private static IPluginHost _host;
        private static readonly List<PatchSpec> Specs = new List<PatchSpec>();

        internal static void Log(string message)
        {
            try { if (_host != null) _host.Log("[Example] " + message); } catch { }
        }

        /// <summary>
        /// The launcher assembly, resolved lazily.
        ///
        /// It cannot be captured while declaring patches: on PCL-CE this runs before
        /// the launcher assembly is loaded. The hooks only need it once they run.
        /// </summary>
        private static Assembly Target
        {
            get
            {
                if (_target != null) return _target;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (a.GetName().Name == "Plain Craft Launcher 2") { _target = a; break; }
                    }
                    catch { }
                }
                return _target;
            }
        }

        public IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion, IPluginHost host)
        {
            _host = host;
            Specs.Clear();
            Log("declaring patches for host=" + hostKind + " version=" + hostVersion);

            string sel = null;
            try { sel = Environment.GetEnvironmentVariable("PCLHARMONY_TEST_SITES"); } catch { }
            if (string.IsNullOrEmpty(sel)) sel = "1,2,3,4,5,6,7,8";
            Func<int, bool> on = delegate (int n)
            {
                foreach (var s in sel.Split(','))
                    if (s.Trim() == n.ToString()) return true;
                return false;
            };

            if (string.Equals(hostKind, "ce", StringComparison.OrdinalIgnoreCase))
            {
                DeclareCePatches();
            }
            else
            {
                if (on(1)) PatchSite1_NoArgEntry();
                if (on(2)) PatchSite2_DispatchRedirect();
                if (on(3)) PatchSite3_RealClientListLoader();
                if (on(4)) PatchSite4_ShowCardMinecraft();
                if (on(5)) PatchSite5_InstallRow();
                if (on(6)) PatchSite6_DownloadEntry();
                if (on(7)) PatchSite7_ResourceButtons();
                if (on(8)) PatchSite8_EmptyStateButtons();
            }

            Log("declared " + Specs.Count + " patch spec(s)");
            return Specs.ToArray();
        }

        /// <summary>
        /// Declares one patch. The host resolves the target type and method and does
        /// the patching; this only describes what should happen.
        /// </summary>
        private static bool Declare(string kind, string typeName, string methodName,
                                    string hookName, params string[] parameterTypes)
        {
            Specs.Add(new PatchSpec
            {
                HookKind = kind,
                TypeName = typeName,
                MethodName = methodName,
                HookTypeName = typeof(ExamplePlugin).FullName,
                HookMethodName = hookName,
                TargetParameterTypes = parameterTypes,
            });
            return true;
        }

        // =================================================================
        // CE-side declarations
        //
        // These targets only exist on PCL-CE, so the specs are declared for
        // hostKind "ce" alone. The first rebuilds the value the method produced;
        // the second forces a boolean result.
        // =================================================================
        private static void DeclareCePatches()
        {
            Declare("postfix", "PCL.Core.App.Region.RegionPolicy", "Evaluate",
                    "CeAfterRegionEvaluate");
            // The property getter form of the same answer, for builds that consult
            // it instead of, or as well as, the method above.
            Declare("postfix", "PCL.Core.App.Localization.Lang", "get_IsFeaturesUnrestricted",
                    "CeAfterUnrestrictedCheck", new string[0]);
        }

        /// <summary>
        /// Rebuilds the returned value through its own constructor.
        ///
        /// The type cannot be referenced (it lives in the launcher), so the value is
        /// rebuilt from a member looked up by name plus a plain string.
        /// </summary>
        public static void CeAfterRegionEvaluate(ref object __result)
        {
            try
            {
                if (__result == null) return;
                Type assessment = __result.GetType();

                PropertyInfo classification = assessment.GetProperty("Classification");
                if (classification == null) { Log("ce: no Classification property"); return; }

                object other = Enum.Parse(classification.PropertyType, "Other");
                ConstructorInfo ctor = assessment.GetConstructor(
                    new[] { classification.PropertyType, typeof(string) });
                if (ctor == null) { Log("ce: no (classification, string) constructor"); return; }

                __result = ctor.Invoke(new[] { other, "patched" });
                Log("ce: region assessment replaced (Other)");
            }
            catch (Exception ex)
            {
                Log("ce: region patch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Forces the boolean result to true.
        /// </summary>
        public static void CeAfterUnrestrictedCheck(ref bool __result)
        {
            if (__result) return;
            __result = true;
            Log("ce: boolean result forced");
        }

        // =================================================================
        // shared reflection helpers
        // =================================================================
        private static Type T(string full)
        {
            var t = Target == null ? null : Target.GetType(full, false);
            if (t == null) Log("[Example] !! type missing: " + full);
            return t;
        }

        private static MethodInfo M(Type t, string name, BindingFlags flags, Type[] args)
        {
            if (t == null) return null;
            try
            {
                var mi = args == null ? t.GetMethod(name, flags)
                                      : t.GetMethod(name, flags, null, args, null);
                if (mi == null) Log("[Example] !! method missing: " + t.FullName + "." + name);
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
                Log("[Example] !! ambiguous: " + t.FullName + "." + name);
                return null;
            }
        }




        // =================================================================
        // SITE 1 - parameterless entry on the modpack module
        // Official IL is just `Hint(LegacyNotice)`. Original source prompts for a
        // pack file and runs the real install on a background thread.
        // =================================================================
        private static bool PatchSite1_NoArgEntry()
        {
            var t = T("PCL.ModModpack");
            var mi = M(t, "ModpackInstall", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
            if (mi == null) { Log("SKIP site1"); return false; }
            return Declare("prefix", "PCL.ModModpack", "ModpackInstall", "Site1_Prefix",
                           new string[0]);
        }

        public static bool Site1_Prefix()
        {
            try
            {
                string file = SelectModpackFile();
                Log("[Example] site1 picked file: " + (file ?? "(cancelled)"));
                if (string.IsNullOrEmpty(file)) { Hint(LegacyNotice, true); return false; }

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
                        Log("[Example] site1 install error: "
                                      + (tie.InnerException ?? tie).Message);
                    }
                };

                var run = FindMethod(t.BaseType ?? t, "ModBase", "RunInNewThread");
                if (run != null) run.Invoke(null, new object[] { work, "ModpackInstall", null });
                else work();
            }
            catch (Exception ex)
            {
                Log("[Example] site1 prefix error: " + ex.GetType().Name + ": " + ex.Message);
            }
            return false;
        }

        // =================================================================
        // SITE 2 - three-argument dispatch on the modpack module
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
            var broken = M(t, "ModpackInstall", BindingFlags.Public | BindingFlags.Static,
                           new[] { typeof(string), typeof(string), typeof(string) });
            if (broken == null) { Log("SKIP site2"); return false; }

            // The target cannot be patched directly (see above), so the host rewrites
            // every call site to our replacement. The replacement takes the same
            // arguments and returns object; the host casts the result back to the
            // target's declared return type, keeping the stack shape identical.
            return Declare("redirect", "PCL.ModModpack", "ModpackInstall", "DispatchCore",
                           "String", "String", "String");
        }
        public static object DispatchCore(string File, string InstanceName, string Logo)
        {
            try
            {
                var t = _target.GetType("PCL.ModModpack", true);
                Log("[Example] dispatch: file=" + File + " name=" + (InstanceName ?? "(auto)"));

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
                Log("[Example] dispatch: packType=" + packType + " -> " + helper
                              + " base='" + baseFolder + "'");
                DispatchLastHelper = helper;

                var hm = FindStatic(t, helper);
                if (hm == null) { Log("[Example] !! helper missing: " + helper); return null; }

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

                Log("[Example] dispatch: " + helper + " -> "
                              + (result == null ? "null" : result.GetType().Name));
                return result;
            }
            catch (TargetInvocationException tie)
            {
                Log("[Example] dispatch error: " + (tie.InnerException ?? tie).Message);
                return null;
            }
            catch (Exception ex)
            {
                Log("[Example] dispatch error: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static MethodInfo FindStatic(Type t, string name)
        {
            foreach (var m in t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
                if (m.Name == name) return m;
            return null;
        }

        /// <summary>Ported from the module's detection block.</summary>
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
                Log("[Example] detect failed: " + ex.GetType().Name + ": " + ex.Message);
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
                Log("[Example] OpenZip failed: " + ex.GetType().Name + ": " + ex.Message);
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
                if (dialogs == null) { Log("[Example] !! dialogs type missing"); return null; }

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
                Log("[Example] SelectModpackFile failed: " + ex.GetType().Name + ": " + ex.Message);
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
        // Official IL wires a task that throws on start. Original source
        // wires ModDownload.DlClientListLoader.
        // =================================================================
        private static bool PatchSite3_RealClientListLoader()
        {
            var t = T("PCL.PageDownloadInstall");
            var mi = M(t, "LoaderInit", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (mi == null) { Log("SKIP site3"); return false; }
            return Declare("prefix", "PCL.PageDownloadInstall", "LoaderInit", "Site3_Prefix",
                           new string[0]);
        }

        public static bool Site3_Prefix(object __instance)
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
                if (loader == null) { Log("[Example] !! DlClientListLoader missing"); return true; }

                // IMPORTANT: LoadMinecraft_OnFinish() takes NO parameters, but
                // PageLoaderInit wants Action(Of LoaderTask(Of TIn,TOut)).
                // Delegate.CreateDelegate cannot bind those two shapes and
                // silently returns null (with throwOnBindFailure:false), which
                // leaves the page permanently in "loading" state - the content
                // area stays collapsed and the download page looks empty.
                // The official IL wraps it in a lambda; emulate that by building
                // a delegate over a shim method that ignores its argument.
                var onFinish = pageType.GetMethod("LoadMinecraft_OnFinish",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (onFinish == null)
                {
                    Log("[Example] !! LoadMinecraft_OnFinish missing");
                    return true;
                }
                if (onFinish.GetParameters().Length != 0)
                {
                    Log("[Example] !! LoadMinecraft_OnFinish has parameters: unexpected shape");
                    return true;
                }

                var actionType = typeof(Action<>).MakeGenericType(loader.GetType());
                Delegate finished = BuildNoArgAction(__instance, onFinish, loader.GetType());
                if (finished == null)
                {
                    Log("[Example] !! could not build finished callback delegate");
                    return true;
                }

                var pli = FindPageLoaderInit(baseType, loader.GetType());
                if (pli == null) { Log("[Example] !! PageLoaderInit missing"); return true; }

                pli.Invoke(__instance, new object[]
                {
                    loadMinecraft, panLoad, panAllBack, null, loader, finished, null, true
                });

                // Record what we actually wired, so the loader's state can be
                // inspected from outside (this is the observable proof that the
                // page is no longer bound to the throwing stub task).
                try
                {
                    var nameField = loader.GetType().GetField("Name",
                        BindingFlags.Public | BindingFlags.Instance);
                    string loaderName = nameField == null ? "(?)" : (nameField.GetValue(loader) as string);
                    Site3WiredLoaderName = loaderName;
                    Site3WiredLoaderType = loader.GetType().Name;
                    Log("[Example] site3 wired real loader: name='" + loaderName
                                  + "' type=" + Site3WiredLoaderType
                                  + " (throwing stub in the official build)");
                }
                catch { }
            }
            catch (Exception ex)
            {
                Log("[Example] site3 error: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            return false;
        }

        /// <summary>Observable proof recorded by site 3 (null when never invoked).</summary>
        public static string Site3WiredLoaderName;
        public static string Site3WiredLoaderType;

        // Observable state for external verification (see ExamplePluginProbe).
        public static int Site4Visits;
        public static string Site4LastVisibility;
        public static int DispatchCalls;
        public static int DispatchLastPackType = -1;
        public static string DispatchLastHelper;

        /// <summary>
        /// Builds an <see cref="Action{T}"/> that ignores its argument and calls
        /// the parameterless target method.
        ///
        /// Needed because <c>LoadMinecraft_OnFinish()</c> takes no parameters
        /// while <c>PageLoaderInit</c> wants <c>Action(Of LoaderTask(Of TIn,TOut))</c>.
        /// <see cref="Delegate.CreateDelegate(Type,object,MethodInfo,bool)"/> cannot
        /// bind those shapes and returns null when asked not to throw - which
        /// silently leaves the page stuck in its loading state (the download
        /// page then renders as an empty area). The official IL wraps the call
        /// in exactly this kind of adapter lambda.
        /// </summary>
        private static Delegate BuildNoArgAction(object target, MethodInfo noArgMethod, Type parameterType)
        {
            // Compile: (param p) => { noArgMethod.Invoke(target, null); }
            var p = System.Linq.Expressions.Expression.Parameter(parameterType, "task");
            var call = System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Constant(target),
                noArgMethod);
            var body = System.Linq.Expressions.Expression.Block(call,
                System.Linq.Expressions.Expression.Empty());
            var lambdaType = typeof(Action<>).MakeGenericType(parameterType);
            return System.Linq.Expressions.Expression.Lambda(lambdaType, body, p).Compile();
        }

        private static MethodInfo FindPageLoaderInit(Type baseType, Type loaderType)        {
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
        // Official IL: the card is always Collapsed.
        // Source: the original condition yields Visible.
        // =================================================================
        private static bool PatchSite4_ShowCardMinecraft()
        {
            var t = T("PCL.PageDownloadModify");
            var mi = M(t, "LoadInstanceFinished", BindingFlags.NonPublic | BindingFlags.Instance, null);
            if (mi == null) { Log("SKIP site4"); return false; }
            return Declare("postfix", "PCL.PageDownloadModify", "LoadInstanceFinished", "Site4_Postfix",
                           (string[])null);
        }

        public static void Site4_Postfix(object __instance)
        {
            try
            {
                object card = GetProp(__instance.GetType(), __instance, "CardMinecraft");
                if (card == null) { Log("[Example] site4: CardMinecraft is null"); return; }
                var vis = card.GetType().GetProperty("Visibility");
                vis.SetValue(card, EnumValue(vis.PropertyType, "Visible"), null);
                Site4Visits++;
                Site4LastVisibility = vis.GetValue(card, null) == null ? "(null)" : vis.GetValue(card, null).ToString();
                Log("[Example] site4 CardMinecraft -> Visible (visit #" + Site4Visits + ")");
            }
            catch (Exception ex)
            {
                Log("[Example] site4 error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

                // =================================================================
        // SITE 5 - list-row behaviour
        //
        // Hook point: the method that builds a row from a list entry. Upgrades the
        // row so its primary action matches what the original source did.
        // =================================================================

        private const int ResourceTypesModPack = 2;   // verified: ResourceProject maps /modpacks/ -> (ResourceTypes)2

        public static int Site5Invoked;
        public static int Site5Upgraded;
        public static string Site5LastState = "(none)";
        private static int _site5LastLoggedType = int.MinValue;

        // Delegate types declared by ToListItem's two handler parameters.
        // Cached when the patch is installed so the prefix can build a correct
        // adapter even when a parameter arrives as null.
        private static Type _site5RowClickType;    // PCL.MyListItem/ClickEventHandler
        private static Type _site5SaveClickType;   // PCL.MyIconButton/ClickEventHandler

        private static bool PatchSite5_InstallRow()
        {
            var versionType = T("PCL.ResourceVersion");
            if (versionType == null) return false;

            MethodInfo toListItem = null;
            foreach (var m in versionType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "ToListItem") continue;
                var ps = m.GetParameters();
                if (ps.Length == 3) { toListItem = m; break; }
            }
            if (toListItem == null) { Log("[Example] site5: ToListItem not found"); return false; }

            // Cache the exact delegate types this method declares, so the prefix
            // can build correctly-typed adapters even when an argument is null.
            try
            {
                var tps = toListItem.GetParameters();
                if (tps.Length == 3)
                {
                    _site5RowClickType = tps[0].ParameterType;
                    _site5SaveClickType = tps[1].ParameterType;
                    Log("[Example] site5 handler types: row=" + _site5RowClickType.Name
                                  + " (decl " + _site5RowClickType.DeclaringType.Name + ")"
                                  + " save=" + _site5SaveClickType.Name
                                  + " (decl " + _site5SaveClickType.DeclaringType.Name + ")");
                }
            }
            catch (Exception ex)
            {
                Log("[Example] site5: could not read handler types: " + ex.Message);
            }

            if (toListItem == null) { Log("SKIP site5"); return false; }
            return Declare("prefix", "PCL.ResourceVersion", "ToListItem", "Site5_ToListItem_Prefix",
                           (string[])null);
        }


        /// <summary>
        /// Runs before ResourceVersion.ToListItem.
        ///
        /// Parameter names must match the original method exactly - Harmony binds
        /// by name, not by position. The real names are "ClickHandler" and
        /// "SaveAsButtonHandler"; positional placeholders such as __0 do not exist
        /// and would silently leave both arguments untouched.
        ///
        /// The decision uses the ResourceVersion instance itself
        /// (ResourceVersion.ResourceType is a public field), not the active page:
        /// the caller is lazily invoked and the page lookup proved unreliable.
        /// Only ModPack rows are upgraded; every other resource type keeps its
        /// original save-only behaviour.
        /// </summary>
        public static void Site5_ToListItem_Prefix(object __instance,
                                                    ref object ClickHandler,
                                                    ref object SaveAsButtonHandler)
        {
            try
            {
                Site5Invoked++;
                if (Site5Invoked <= 3)
                    Log("[Example] site5 prefix invoked #" + Site5Invoked
                                  + " version=" + (__instance == null ? "(null)" : __instance.GetType().Name));

                int resType = GetVersionResourceType(__instance);
                Site5LastState = "resType=" + resType;

                // Record every resource type seen in the wild. This is how the
                // patch chain is proven live in a real UI even when the ModPack
                // page itself does not render (see README limitations).
                if (Site5Invoked <= 5 || resType != _site5LastLoggedType)
                {
                    _site5LastLoggedType = resType;
                    Log("[Example] site5 ToListItem invoked #" + Site5Invoked
                                  + " resourceType=" + resType
                                  + " clickHandler="
                                  + (ClickHandler == null ? "(null)" : ClickHandler.GetType().Name));
                }
                if (resType != ResourceTypesModPack) return;

                // The page instance is exactly the target of the delegate PCL is
                // passing in: it is Save_Click bound to the PageDownloadResourceDetail
                // that is currently rendering. Using it avoids guessing at static
                // fields, which are not populated in every host (and are null
                // before the page has been created).
                object page = GetDelegateTarget(ClickHandler) ?? GetDelegateTarget(SaveAsButtonHandler);
                if (page == null) page = FindActiveResourceDetailPage();
                if (page == null)
                {
                    Site5LastState = "resType=2 but no page instance";
                    if (Site5Invoked <= 3)
                        Log("[Example] site5: no page instance available, keeping save-only row");
                    return;
                }

                // The two parameters are DIFFERENT delegate types:
                //   ClickHandler        : PCL.MyListItem/ClickEventHandler
                //   SaveAsButtonHandler : PCL.MyIconButton/ClickEventHandler
                // Derive them from the currently declared parameter types, which is
                // always correct regardless of which assembly version is loaded.
                Type rowClickType = ClickHandler == null ? null : ClickHandler.GetType();
                if (rowClickType == null) rowClickType = _site5RowClickType;
                Type saveClickType = SaveAsButtonHandler == null ? null : SaveAsButtonHandler.GetType();
                if (saveClickType == null) saveClickType = _site5SaveClickType;

                if (rowClickType == null || saveClickType == null)
                {
                    Site5LastState = "delegate types unresolved";
                    Log("[Example] site5: cannot resolve delegate types, rows stay save-only");
                    return;
                }

                Delegate install = BuildClickAdapter(page.GetType(), page, "Install_Click", rowClickType);
                Delegate save = BuildClickAdapter(page.GetType(), page, "Save_Click", saveClickType);
                if (install == null || save == null)
                {
                    Site5LastState = "adapter build failed";
                    Log("[Example] site5: could not build click handlers, rows stay save-only");
                    return;
                }

                ClickHandler = install;        // row click   -> Install_Click
                SaveAsButtonHandler = save;    // save button -> Save_Click

                Site5Upgraded++;
                Site5LastState = "upgraded";
                if (Site5Upgraded <= 3 || Site5Upgraded % 25 == 0)
                    Log("[Example] site5 upgraded row #" + Site5Upgraded
                                  + " to install+save (resourceType=" + resType + ")");
            }
            catch (Exception ex)
            {
                Site5LastState = "error: " + ex.GetType().Name;
                Log("[Example] site5 prefix error (row left unchanged): "
                              + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Returns the delegate's target instance, or null.</summary>
        private static object GetDelegateTarget(object candidate)
        {
            try
            {
                var d = candidate as Delegate;
                return d == null ? null : d.Target;
            }
            catch { return null; }
        }

        // =================================================================
        // SITE 6 - the download page's left-hand entries
        //
        // The shipped constructor collapses both entries and points the page at a
        // sub-page that is not the first one. Everything below restores them after
        // the constructor has run, so it works for the first instance and for any
        // later one too.
        // =================================================================
        private static bool PatchSite6_DownloadEntry()
        {
            var t = T("PCL.PageDownloadLeft");
            if (t == null) { Log("SKIP site6"); return false; }
            return Declare("postfix", "PCL.PageDownloadLeft", ".ctor", "Site6_Postfix",
                           new string[0]);
        }

        public static void Site6_Postfix(object __instance)
        {
            try
            {
                var type = __instance.GetType();

                var install = GetProp(type, __instance, "ItemInstall");
                var resource = GetProp(type, __instance, "LabResource");
                var mod = GetProp(type, __instance, "ItemMod");
                var panItem = GetProp(type, __instance, "PanItem");

                SetVisible(install);
                SetVisible(resource);

                // The constructor checks a different entry than the one it starts on,
                // so the radio state has to be moved back as well.
                SetChecked(mod, false);
                SetChecked(install, true);

                // Put the entry back into the list. It stays a child of the same
                // panel, so ordering relative to the others is preserved by index.
                if (panItem != null && install != null && !ChildrenContains(panItem, install))
                {
                    InsertChild(panItem, install, 0);
                    Site6Inserted++;
                }

                // Point the page at the first sub-page again. This only changes the
                // field; creating the page here would re-enter the constructor.
                var pageIdField = type.GetField("PageID",
                    BindingFlags.Public | BindingFlags.Instance);
                if (pageIdField != null)
                    pageIdField.SetValue(__instance, EnumValue(pageIdField.FieldType, "DownloadInstall"));

                Site6Visits++;
                if (Site6Visits <= 3)
                    Log("site6: download entries restored (visit #" + Site6Visits + ")");
            }
            catch (Exception ex)
            {
                Log("site6 error (page left as shipped): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static int Site6Visits;
        public static int Site6Inserted;

        /// <summary>
        /// Reveals a control unless it is already visible, and records nothing when
        /// the control is absent.
        /// </summary>
        private static void SetVisible(object element)
        {
            try
            {
                if (element == null) return;
                var p = element.GetType().GetProperty("Visibility",
                    BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return;
                object visible = EnumValue(p.PropertyType, "Visible");
                if (p.GetValue(element, null).Equals(visible)) return;
                p.SetValue(element, visible, null);
            }
            catch (Exception ex)
            {
                Log("[Example] SetVisible failed: " + ex.Message);
            }
        }

        private static void SetChecked(object element, bool value)
        {
            try
            {
                if (element == null) return;
                var p = element.GetType().GetProperty("Checked",
                    BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return;
                p.SetValue(element, value, null);
            }
            catch (Exception ex)
            {
                Log("[Example] SetChecked failed: " + ex.Message);
            }
        }

        private static bool ChildrenContains(object panel, object child)
        {
            try
            {
                var children = GetChildren(panel);
                if (children == null) return false;
                var contains = children.GetType().GetMethod("Contains");
                if (contains == null) return false;
                return (bool)contains.Invoke(children, new[] { child });
            }
            catch (Exception ex)
            {
                Log("[Example] ChildrenContains failed: " + ex.Message);
                return false;
            }
        }

        private static void InsertChild(object panel, object child, int index)
        {
            try
            {
                var children = GetChildren(panel);
                if (children == null) return;
                var insert = children.GetType().GetMethod("Insert");
                if (insert == null)
                {
                    var add = children.GetType().GetMethod("Add");
                    if (add != null) add.Invoke(children, new[] { child });
                    return;
                }
                insert.Invoke(children, new[] { (object)index, child });
            }
            catch (Exception ex)
            {
                Log("[Example] InsertChild failed: " + ex.Message);
            }
        }

        // =================================================================
        // SITE 7 - the resource page's install button
        //
        // The page type setter forces the button hidden on every change, so the
        // postfix runs after that assignment and re-evaluates it.
        // =================================================================
        private static bool PatchSite7_ResourceButtons()
        {
            var t = T("PCL.PageResource");
            if (t == null) { Log("SKIP site7"); return false; }
            return Declare("postfix", "PCL.PageResource", "set_PageType", "Site7_Postfix",
                           // One parameter (the new value); null = do not constrain it, so the
                           // setter is selected regardless of the enum type name.
                           (string[])null);
        }

        public static void Site7_Postfix(object __instance)
        {
            try
            {
                var type = __instance.GetType();

                // Read the field rather than the property: the setter has already
                // stored the new value by the time a postfix runs.
                var pageType = GetProp(type, __instance, "PageType");
                var button = GetProp(type, __instance, "BtnSearchInstallModPack");
                if (pageType == null || button == null) return;

                // Only meaningful for the modpack listing; every other type keeps the
                // button hidden exactly as shipped.
                if (Convert.ToInt32(pageType) != Site7ModPackType) return;

                SetVisible(button);
                Site7Visits++;
                if (Site7Visits <= 3)
                    Log("site7: install button restored (visit #" + Site7Visits + ")");
            }
            catch (Exception ex)
            {
                Log("site7 error (button left as shipped): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static int Site7Invoked;
        public static int Site7Visits;

        private static int _site7ModPackType = 2;
        private static int Site7ModPackType { get { return _site7ModPackType; } }

        // =================================================================
        // SITE 8 - the empty-state download button
        // =================================================================
        private static bool PatchSite8_EmptyStateButtons()
        {
            var t = T("PCL.PageSelectRight");
            if (t == null) { Log("SKIP site8"); return false; }
            return Declare("postfix", "PCL.PageSelectRight", "McInstanceListUI",
                           "Site8_Postfix", (string[])null);
        }

        public static void Site8_Postfix(object __instance)
        {
            try
            {
                var button = GetProp(__instance.GetType(), __instance, "BtnEmptyDownload");
                if (button == null) return;
                SetVisible(button);
                Site8Visits++;
                if (Site8Visits <= 3)
                    Log("site8: empty-state button restored (visit #" + Site8Visits + ")");
            }
            catch (Exception ex)
            {
                Log("site8 error (button left as shipped): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static int Site8Visits;

        // --- reflection over WPF Children collections -----------------------
        private static object GetChildren(object element)
        {
            if (element == null) return null;
            var p = element.GetType().GetProperty("Children");
            return p == null ? null : p.GetValue(element, null);
        }

        private static int ChildrenCount(object children)
        {
            if (children == null) return -1;
            var p = children.GetType().GetProperty("Count");
            return p == null ? -1 : Convert.ToInt32(p.GetValue(children, null));
        }

        /// <summary>Reads ResourceVersion.ResourceType (public field).</summary>
        private static int GetVersionResourceType(object version)
        {
            try
            {
                if (version == null) return -1;
                var f = version.GetType().GetField("ResourceType",
                    BindingFlags.Public | BindingFlags.Instance);
                if (f == null) return -1;
                object v = f.GetValue(version);
                return v == null ? -1 : Convert.ToInt32(v);
            }
            catch { return -1; }
        }

        /// <summary>
        /// Returns the PageDownloadResourceDetail instance.
        ///
        /// It is a STATIC field on PCL.ModMain (ModMain.FrmDownloadResourceDetail),
        /// not an instance field on FormMain - looking it up on FormMain silently
        /// found nothing, which is why the upgrade never happened.
        ///
        /// Reaching this code at all already implies the resource detail page is
        /// the one rendering (the only caller is that page's card content
        /// builder), so no extra "is the page active" check is needed.
        /// </summary>
        private static object FindActiveResourceDetailPage()
        {
            try
            {
                var modMain = _target.GetType("PCL.ModMain", false);
                if (modMain == null) { Site5LastState = "ModMain missing"; return null; }

                var f = modMain.GetField("FrmDownloadResourceDetail",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null)
                {
                    object detail = f.GetValue(null);
                    if (detail != null) return detail;
                    Site5LastState = "detail page null";
                    return null;
                }

                // Fallback: any static field of that page type.
                foreach (var sf in modMain.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                     | BindingFlags.Static))
                {
                    if (sf.FieldType == null) continue;
                    if (!string.Equals(sf.FieldType.Name, "PageDownloadResourceDetail",
                                       StringComparison.Ordinal)) continue;
                    object v = sf.GetValue(null);
                    if (v != null) return v;
                }

                Site5LastState = "detail page field missing";
                return null;
            }
            catch (Exception ex)
            {
                Site5LastState = "lookup error: " + ex.GetType().Name;
                return null;
            }
        }

        private static bool IsHandlerFor(object page, object handler, string methodName)
        {
            try
            {
                var d = handler as Delegate;
                if (d == null) return false;
                return d.Method != null
                       && string.Equals(d.Method.Name, methodName, StringComparison.Ordinal)
                       && ReferenceEquals(d.Target, page);
            }
            catch { return false; }
        }

        /// <summary>
        /// Builds <c>(object sender, MouseButtonEventArgs e) =&gt; handler((MyListItem)sender, e)</c>
        /// for MyListItem.ClickEventHandler.
        ///
        /// Needed because the install handler's first parameter is MyListItem, not
        /// object: Delegate.CreateDelegate cannot bind that shape and returns
        /// null when told not to throw - the same failure mode that once left
        /// the download page blank.
        /// </summary>
        /// <summary>
        /// Builds a delegate of <paramref name="delegateType"/> that forwards to
        /// <paramref name="methodName"/> on <paramref name="page"/>.
        ///
        /// <paramref name="delegateType"/> must be the EXACT nested delegate type
        /// the target parameter declares:
        ///   ToListItem param 0 is PCL.MyListItem/ClickEventHandler
        ///   ToListItem param 1 is PCL.MyIconButton/ClickEventHandler
        /// Building one for the other produces InvalidCastException later
        /// ("[A]ClickEventHandler cannot be cast to [B]ClickEventHandler") when WPF
        /// assigns it to MyIconButton.Click.
        ///
        /// The first argument is cast to the handler's first parameter type, and
        /// the second (MouseButtonEventArgs) converts to EventArgs implicitly.
        /// </summary>
        private static Delegate BuildClickAdapter(Type pageType, object page, string methodName,
                                                  Type delegateType)
        {
            if (delegateType == null)
            {
                Log("[Example] site5 !! " + methodName + ": delegate type unknown");
                return null;
            }

            var handler = pageType.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (handler == null)
            {
                Log("[Example] site5 !! " + methodName + " missing");
                return null;
            }

            var ps = handler.GetParameters();
            if (ps.Length != 2)
            {
                Log("[Example] site5 !! " + methodName + " has " + ps.Length + " params");
                return null;
            }

            var invoke = delegateType.GetMethod("Invoke");
            if (invoke == null)
            {
                Log("[Example] site5 !! " + delegateType.Name + " has no Invoke");
                return null;
            }
            var dparams = invoke.GetParameters();
            if (dparams.Length != 2)
            {
                Log("[Example] site5 !! " + delegateType.Name + " Invoke arity "
                              + dparams.Length);
                return null;
            }

            var senderParam = System.Linq.Expressions.Expression.Parameter(dparams[0].ParameterType, "sender");
            var argsParam = System.Linq.Expressions.Expression.Parameter(dparams[1].ParameterType, "e");

            // sender: only cast when the handler genuinely needs it; Save_Click
            // takes object and handles the unwrapping itself.
            var senderArg = ps[0].ParameterType == typeof(object) || ps[0].ParameterType.IsAssignableFrom(dparams[0].ParameterType)
                ? (System.Linq.Expressions.Expression)senderParam
                : System.Linq.Expressions.Expression.Convert(senderParam, ps[0].ParameterType);

            var argsArg = ps[1].ParameterType.IsAssignableFrom(dparams[1].ParameterType)
                ? (System.Linq.Expressions.Expression)argsParam
                : System.Linq.Expressions.Expression.Convert(argsParam, ps[1].ParameterType);

            var call = System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Constant(page), handler, senderArg, argsArg);

            var body = System.Linq.Expressions.Expression.Block(call,
                System.Linq.Expressions.Expression.Empty());

            var adapter = System.Linq.Expressions.Expression
                .Lambda(delegateType, body, senderParam, argsParam)
                .Compile();

            if (adapter == null)
                Log("[Example] site5 !! adapter for " + methodName + " is null");
            return adapter;
        }

        /// <summary>
        /// Resolves a nested delegate type by name, preferring the one declared on
        /// the type the parameter actually expects (found from ToListItem's
        /// signature) and falling back to a plugin-side type name.
        /// </summary>
        private static Type ResolveNestedDelegate(Type declaringType, string name)
        {
            try
            {
                if (declaringType != null)
                {
                    var t = declaringType.GetNestedType(name,
                        BindingFlags.Public | BindingFlags.NonPublic);
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }


        /// <summary>
        /// Reads the private ResourceTypes field; mirrors the official comparison
        /// against ModPack.
        /// </summary>
        private static int GetTargetResourceType(object page)
        {
            try
            {
                var t = page.GetType();
                while (t != null)
                {
                    var f = t.GetField("TargetResourceType",
                        BindingFlags.NonPublic | BindingFlags.Public
                        | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f != null)
                    {
                        object v = f.GetValue(page);
                        return v == null ? -1 : Convert.ToInt32(v);
                    }
                    t = t.BaseType;
                }
            }
            catch { }
            return -1;
        }

        // =================================================================
        // misc
        // =================================================================
        private static object GetProp(Type t, object instance, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // WPF's x:Name generates a FIELD in C# but a PROPERTY in VB, and the two
            // launchers are written in different languages - so check both.
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance);

            var p = t.GetProperty(name, flags);
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
                Log("[Example] Hint failed: " + ex.Message);
            }
        }
    }
}
