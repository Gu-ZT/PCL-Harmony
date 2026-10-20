using System;
using System.Reflection;
using HarmonyLib;

namespace PclHarmony
{
    /// <summary>
    /// Contract implemented by every plugin found in &lt;exe dir&gt;\plugins\*.dll.
    /// Instances must have a public parameterless constructor.
    /// </summary>
    public interface IPlugin
    {
        /// <summary>Stable, human readable plugin name (used as the Harmony instance id).</summary>
        string Name { get; }

        /// <summary>
        /// Called once, on the thread that is loading the launcher assembly, before the
        /// launcher's own Main() runs. Patches applied here are in place for the whole run.
        /// </summary>
        /// <param name="harmony">Per-plugin Harmony instance, already named after the plugin.</param>
        /// <param name="target">The launcher assembly ("Plain Craft Launcher 2").</param>
        void Load(Harmony harmony, Assembly target);
    }
}
