using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PclHarmony
{
    /// <summary>
    /// Injected into the launcher's own AppDomain through
    /// &lt;appDomainManagerAssembly&gt;PclHarmony&lt;/appDomainManagerAssembly&gt; and
    /// &lt;appDomainManagerType&gt;PclHarmony.Entry&lt;/appDomainManagerType&gt; inside
    /// "Plain Craft Launcher 2.exe.config".
    ///
    /// Responsibilities:
    ///   1. wait for the launcher assembly to load,
    ///   2. install the embedded-dependency resolver EARLY (Harmony needs launcher
    ///      metadata before PCL's own resolver in Application.New() exists),
    ///   3. discover and load plugins from &lt;exe dir&gt;\plugins\*.dll,
    ///   4. never let a plugin failure abort launcher startup.
    /// </summary>
    public class Entry : AppDomainManager
    {
        private const string TargetAssemblyName = "Plain Craft Launcher 2";

        private static string _baseDirectory;
        private static Assembly _target;
        private static bool _disabled;
        private static int _started;

        /// <summary>
        /// CRITICAL: no file I/O, no logging, no reflection in here.
        ///
        /// InitializeNewDomain runs before the CLR has finished bringing up the
        /// domain. Touching the filesystem from this method corrupts the launcher's
        /// own startup (its folder-permission check then fails with
        /// "路径中具有非法字符" and PCL shows "运行环境错误"). Only plain field
        /// assignment and event subscription are allowed; logging is opened lazily
        /// on first use, which happens later from the AssemblyLoad handler.
        /// </summary>
        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            try
            {
                string baseDir = null;
                if (appDomainInfo != null) baseDir = appDomainInfo.ApplicationBase;
                if (string.IsNullOrEmpty(baseDir)) baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _baseDirectory = baseDir;

                string disable = Environment.GetEnvironmentVariable("PCLHARMONY_DISABLE");
                _disabled = disable == "1"
                            || string.Equals(disable, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Swallow: this method must never throw into the CLR.
            }

            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            }
            catch
            {
            }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                Assembly loaded;
                try { loaded = args.LoadedAssembly; }
                catch { return; }
                if (loaded == null) return;
                if (!string.Equals(loaded.GetName().Name, TargetAssemblyName, StringComparison.Ordinal)) return;
                if (Interlocked.Exchange(ref _started, 1) != 0) return;

                _target = loaded;
                PluginApi.SetTarget(loaded);
                LoaderLog.SetBaseDirectory(_baseDirectory);
                LoaderLog.Info("PclHarmony loader " + typeof(Entry).Assembly.GetName().Version
                               + " attached (ADM PclHarmony.Entry)");
                LoaderLog.Info("target assembly loaded: " + loaded.FullName);

                if (_disabled)
                {
                    LoaderLog.Info("PCLHARMONY_DISABLE=1 -> loader idle, no patch, no plugins");
                    LoaderLog.Flush();
                    return;
                }

                // Must come before any plugin touches launcher metadata.
                EmbeddedResolver.Install(loaded);
                PluginHost.Start(loaded);
            }
            catch (Exception ex)
            {
                // Absolute last line of defence: a loader fault must not stop PCL.
                try
                {
                    LoaderLog.Info("loader startup FAILED (launcher continues): "
                                   + ex.GetType().Name + ": " + ex.Message);
                    LoaderLog.Flush();
                }
                catch { }
            }
        }
    }
}
