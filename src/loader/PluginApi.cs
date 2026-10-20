using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PclHarmony
{
    /// <summary>
    /// Public surface for plugins (Test.dll etc.).
    /// All members are safe to call from any thread and never throw.
    /// </summary>
    public static class PluginApi
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Harmony> Harmonies =
            new Dictionary<string, Harmony>(StringComparer.Ordinal);
        private static readonly Harmony Shared = new Harmony("pcl.harmony.pluginloader");
        private static Assembly _target;

        /// <summary>The launcher assembly ("Plain Craft Launcher 2"). May be null very early.</summary>
        public static Assembly Target
        {
            get { return _target; }
        }

        /// <summary>Shared loader-wide Harmony instance (id "pcl.harmony.pluginloader").</summary>
        public static Harmony SharedHarmony
        {
            get { return Shared; }
        }

        /// <summary>Returns a cached Harmony instance for the given id.</summary>
        public static Harmony GetHarmony(string id)
        {
            if (string.IsNullOrEmpty(id)) return Shared;
            lock (Gate)
            {
                Harmony harmony;
                if (Harmonies.TryGetValue(id, out harmony)) return harmony;
                harmony = new Harmony(id);
                Harmonies[id] = harmony;
                return harmony;
            }
        }

        /// <summary>Writes a line to &lt;exe dir&gt;\PclHarmony.log.</summary>
        public static void Log(string message)
        {
            LoaderLog.Info(message);
        }

        internal static void SetTarget(Assembly target)
        {
            _target = target;
        }

        internal static string GetExecutableDirectory()
        {
            return LoaderLog.BaseDirectory;
        }
    }
}
