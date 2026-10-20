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

            // TLS repair - see RepairTlsDefaults for the full explanation.
            // Runs before the launcher's Main(), i.e. before any networking.
            try
            {
                RepairTlsDefaults();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Injects an AppDomainManager makes the CLR treat the host as a legacy
        /// (pre-v4) framework application. That has two visible consequences,
        /// and this project has to defuse both:
        ///
        ///   1. legacy path handling -> the launcher's <c>\\?\</c> extended paths
        ///      are rejected and it dies with "运行环境错误". Handled by the
        ///      <c>Switch.System.IO.UseLegacyPathHandling=false</c> entry that
        ///      install.ps1 writes into the exe.config.
        ///
        ///   2. legacy TLS defaults -> SecurityProtocol stays at "Ssl3, Tls" and
        ///      never picks up TLS 1.2+, so every HTTPS request fails with
        ///      SecureChannelFailure and the launcher reports
        ///      "你的网络环境不佳…". PCL only sets SecurityProtocol on
        ///      Windows &lt; 8, so on modern Windows it relies on the default and
        ///      never repairs this itself.
        ///
        /// (2) is repaired here rather than in the config so that a
        /// hand-edited or partially merged exe.config cannot break networking.
        /// Protocols are only ever *added*, never removed.
        /// </summary>
        private static void RepairTlsDefaults()
        {
            try
            {
                const System.Net.SecurityProtocolType Tls12 =
                    (System.Net.SecurityProtocolType)3072;    // Tls12
                const System.Net.SecurityProtocolType Tls13 =
                    (System.Net.SecurityProtocolType)12288;   // Tls13 (no-op on older CLR/OS)

                System.Net.SecurityProtocolType current =
                    System.Net.ServicePointManager.SecurityProtocol;
                System.Net.SecurityProtocolType repaired = current | Tls12 | Tls13;

                if (repaired != current)
                    System.Net.ServicePointManager.SecurityProtocol = repaired;
            }
            catch
            {
                // A failed repair must never stop the launcher.
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

                // Belt and braces: re-apply the TLS default repair now that the
                // launcher's own assemblies are coming up. Some hosts reset
                // ServicePointManager during WPF initialisation.
                try
                {
                    System.Net.SecurityProtocolType before =
                        System.Net.ServicePointManager.SecurityProtocol;
                    RepairTlsDefaults();
                    System.Net.SecurityProtocolType after =
                        System.Net.ServicePointManager.SecurityProtocol;
                    LoaderLog.Info("TLS: SecurityProtocol " + before + " -> " + after);
                }
                catch { }

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
